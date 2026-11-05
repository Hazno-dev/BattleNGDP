using System.Text.Json;
using System.Text.Json.Nodes;
using BattleNGDP.Core;

namespace BattleNGDP.TACTChannel;

public static class TACTChannelsService
{
    private static HttpClient s_client = new();
    private static JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true
    };
    
    public static async Task<(TACTChannelProductBuild, bool)> GetLatest(string tact)
    {
        var latestSummary = await GetSummaryPaths();
        if (!TACTChannelsStorage.HasChanged(in latestSummary)) {
            var cur = TACTChannelsStorage.GetTact(tact);
            if (cur.Count > 0) {
                return (cur[0], false);
            }
        }
        
        await UpdateBuilds(tact, latestSummary);
        TACTChannelsStorage.Save();
        
        var curData = TACTChannelsStorage.GetTact(tact);
        if (curData.Count == 0) {
            throw new Exception($"No builds found for {tact}");
        }
        
        return (curData[0], true);
    }

    private static async Task<TACTChannelPaths> GetSummaryPaths()
    {
        using var res = await s_client.GetAsync("https://distribution.version.battle.net/summary");
        res.EnsureSuccessStatusCode();
        return await JsonSerializer.DeserializeAsync<TACTChannelPaths>(await res.Content.ReadAsStreamAsync(), s_options);
    }
    
    private static async Task UpdateBuilds(string tact, TACTChannelPaths builds)
    {
        //check folders list:
        var tactKeys = Directory.GetDirectories(Config.Root);
        tactKeys = [.. tactKeys.Select(x => Path.GetFileName(x))];
        if (!tactKeys.Contains(tact)) {
            Directory.CreateDirectory(Path.Combine(Config.Root, tact));
            tactKeys = [.. tactKeys, tact];
        }
        
        var buildHash = builds.Public.Channel.AsSpan();
        using var res = await s_client.GetAsync($"https://distribution.version.battle.net/{builds.Path}/{buildHash[..2]}/{buildHash[2..4]}/{buildHash}");
        res.EnsureSuccessStatusCode();
        var curSummary = await JsonSerializer.DeserializeAsync<TACTChannelSummary>(await res.Content.ReadAsStreamAsync(), s_options);
        foreach (var product in curSummary.Products) {
            if (!tactKeys.Contains(product.Variant)) {
                continue;
            }
            
            await UpdateBuild(builds.Path, product);
        }
    }

    private static async Task UpdateBuild(string path, TACTChannelProduct product)
    {
        var curData = TACTChannelsStorage.GetTact(product.Variant);
        if (curData.Count == 0) {
            await AddBuild(path, product);
            return;
        }

       
        var latest = curData[0];
        foreach (var build in product.Builds) {
            foreach (var region in build.Branches) {
                if (!latest.Builds.TryGetValue(region, out var latestBuild)) {
                    await AddBuild(path, product);
                    return;
                }

                if (latestBuild.Definition != build.Definition) {
                    await AddBuild(path, product);
                    return;
                }
            }
        }

        foreach (var build in product.Builds) {
            foreach (var region in build.Branches) {
                if (!latest.Builds.TryGetValue(region, out var latestBuild)) {
                    await AddBuild(path, product);
                    return;
                }

                if (latestBuild.Definition != build.Definition) {
                    await AddBuild(path, product);
                    return;
                }
            }
        }

        if (product.Preloads == null) {
            if (latest.Preloads != null) {
                await AddBuild(path, product);
            }

            return;
        }

        if (latest.Preloads == null) {
            await AddBuild(path, product);
            return;
        }

        foreach (var preload in product.Preloads) {
            foreach (var region in preload.Branches) {
                if (!latest.Preloads.TryGetValue(region, out var latestBuild)) {
                    await AddBuild(path, product);
                    return;
                }

                if (latestBuild.Definition != preload.Definition) {
                    await AddBuild(path, product);
                    return;
                }
            }
        }
    }

    private static async Task AddBuild(string path, TACTChannelProduct product)
    {
        var build = new TACTChannelProductBuild() {
            Product = product.Product,
            Variant = product.Variant,
            Infrastructure = product.Infrastructure,
            Builds = new Dictionary<string, TACTChannelDefinitionValuePair<TACTChannelProductDefinitionData>>(),
            Regions = new Dictionary<string, TACTChannelDefinitionValuePair<TACTChannelProductCDNData>>(),
            ObtainedDate = DateTime.UtcNow,
        };

        foreach (var buildDef in product.Builds) {
            var buildHash = buildDef.Definition.AsSpan();
            using var res = await s_client.GetAsync($"https://distribution.version.battle.net/{path}/{buildHash[..2]}/{buildHash[2..4]}/{buildHash}");
            res.EnsureSuccessStatusCode();
            var buildData = await JsonSerializer.DeserializeAsync<TACTChannelProductDefinitionData>(await res.Content.ReadAsStreamAsync(), s_options);
            if (!string.IsNullOrEmpty(buildData.ProductConfig.Hash)) {
                var confHash = buildData.ProductConfig.Hash.AsSpan();
                using var configData = await s_client.GetAsync($"https://distribution.version.battle.net/{path}/{confHash[..2]}/{confHash[2..4]}/{confHash}");
                res.EnsureSuccessStatusCode();
                buildData.ProductConfig = buildData.ProductConfig with {
                    Data = (await JsonNode.ParseAsync(await configData.Content.ReadAsStreamAsync()))?.AsObject()
                };
            }
            
            var kvp = new TACTChannelDefinitionValuePair<TACTChannelProductDefinitionData>() {
                Definition = buildDef.Definition,
                Value = buildData
            };
            
            foreach (var branchRegion in buildDef.Branches) {
                build.Builds.Add(branchRegion, kvp);
            }
        }

        foreach (var cdnDef in product.Regions) {
            var cdnHash = cdnDef.Cdns.AsSpan();
            using var res = await s_client.GetAsync($"https://distribution.version.battle.net/{path}/{cdnHash[..2]}/{cdnHash[2..4]}/{cdnHash}");
            res.EnsureSuccessStatusCode();
            var cdnData = await JsonSerializer.DeserializeAsync<TACTChannelProductCDNData>(await res.Content.ReadAsStreamAsync(), s_options);
            var kvp = new TACTChannelDefinitionValuePair<TACTChannelProductCDNData>() {
                Definition = cdnDef.Cdns,
                Value = cdnData
            };

            foreach (var branchRegion in cdnDef.Branches) {
                build.Regions.Add(branchRegion, kvp);
            }
        }

        if (product.Preloads is { Length: > 0 }) {
            build.Preloads = new Dictionary<string, TACTChannelDefinitionValuePair<TACTChannelProductDefinitionData>>();
            foreach (var preloadDef in product.Preloads) {
                var preloadHash = preloadDef.Definition.AsSpan();
                using var res = await s_client.GetAsync($"https://distribution.version.battle.net/{path}/{preloadHash[..2]}/{preloadHash[2..4]}/{preloadHash}");
                res.EnsureSuccessStatusCode();
                var preloadData = await JsonSerializer.DeserializeAsync<TACTChannelProductDefinitionData>(await res.Content.ReadAsStreamAsync(), s_options);
                var kvp = new TACTChannelDefinitionValuePair<TACTChannelProductDefinitionData>() {
                    Definition = preloadDef.Definition,
                    Value = preloadData
                };
                foreach (var branchRegion in preloadDef.Branches) {
                    build.Preloads.Add(branchRegion, kvp);
                }
            }
        }
        
        var curData = TACTChannelsStorage.GetTact(product.Variant);
        curData.Add(build);
        curData.Sort();
    }
}