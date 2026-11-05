/*using System.Text.Json.Nodes;
using BattleNGDP.BlizzTrack;
using BattleNGDP.CASC;
using BattleNGDP.Core;
using BattleNGDP.TACT;
using BattleNGDP.TACT.CDN;
using BattleNGDP.TACT.Data;
using BattleNGDP.TACT.Manifest;
using Sharprompt;

namespace BattleNGDP.Runner;

public class RunnerTACTProduct
{
    public async Task Run(string product)
    {
        var productInfo = await ProductManager.GetProduct(product);
        if (productInfo.ProductBuilds.Count == 0) {
            throw new Exception($"No builds found for product {product}");
        }
        
        var allBuilds = productInfo.ProductBuilds.Aggregate(new List<ProductBuildInfo>(), (list, build) => {
            list.AddRange(build.Value);
            return list;
        });
        allBuilds.Sort((a, b) => b.Version.CompareTo(a.Version));

        while (true) {
            await Run(product, productInfo, allBuilds);
            Console.ReadLine();
        }
    }

    private async Task Run(string product, ProductInfo productInfo, List<ProductBuildInfo> allBuilds)
    {
        Logger.Log($"Found {allBuilds.Count} builds for {product} ({productInfo.Name})");
        var targetBuild = Prompt.Select("Select product build", allBuilds, textSelector:build => build.CondensedString());
        
        Logger.Log($"Scanning {product} ({productInfo.Name}) v{targetBuild.Version}");
        var loader = new TACTProductLoader(new TACTProductConfig() {
            Name = productInfo.TACTName,
        });
        
        if (targetBuild.Encrypted && string.IsNullOrEmpty(targetBuild.Armadillo)) {
            string? armadilloName = null;
            if (!string.IsNullOrEmpty(targetBuild.ProductConfigHash) && !string.IsNullOrEmpty(targetBuild.ProductConfigPath)) {
                var path = targetBuild.ProductConfigPath.TrimEnd("/data").ToString();
                using var configResponse = await loader.Load(path, "data", targetBuild.ProductConfigHash, null);
                if (configResponse != null) {
                    var configJson = JsonNode.Parse(configResponse.Value.ReadOnlySpan);
                    armadilloName = configJson?["all"]?["config"]?["decryption_key_name"]?.GetValue<string?>();
                }
            }

            if (armadilloName == null) {
                Logger.Log($"Failed to load config for {product} v{targetBuild.Version}");
                armadilloName = Prompt.Input<string>($"v{targetBuild.Version} is encrypted and armadillo name-scan failed. Please enter the armadillo key name");
            }

            if (string.IsNullOrEmpty(armadilloName)) {
                throw new InvalidDataException($"v{targetBuild.Version} is encrypted and provided armadillo name was invalid.");
            }

            targetBuild.Armadillo = armadilloName;
            ProductStorage.Save();
        }

        if (string.IsNullOrEmpty(targetBuild.TACTPath)) {
            var path = Prompt.Input<string>($"v{targetBuild.Version} is missing TACT path. Please enter the TACT path");
            if (string.IsNullOrEmpty(path)) {
                throw new InvalidDataException($"v{targetBuild.Version} is missing TACT path");
            }
            
            targetBuild.TACTPath = path;
            ProductStorage.Save();
        }

        using var buildConfigBuf = await loader.Load(targetBuild.TACTPath, "config", targetBuild.TACTBuildConfigHash, targetBuild.Armadillo);
        if (buildConfigBuf == null) {
            Logger.Log($"Failed to load build config for {product} v{targetBuild.Version}");
            return;
        }
        
        using var cdnConfigBuf = await loader.Load(targetBuild.TACTPath, "config", targetBuild.TACTCDNConfigHash, targetBuild.Armadillo);
        if (cdnConfigBuf == null) {
            Logger.Log($"Failed to load cdn config for {product} v{targetBuild.Version}");
            return;
        }
        
        targetBuild.TACTBuildConfig = new BuildConfig(buildConfigBuf.Value.ReadOnlySpan);
        targetBuild.TACTCDNConfig = new CDNConfig(cdnConfigBuf.Value.ReadOnlySpan);
        ProductStorage.Save();
        
        Logger.Log($"Loaded build config for {product} v{targetBuild.Version}");
        Logger.Log($"");
        Logger.Log(targetBuild.CondensedString());

        var archives = targetBuild.TACTCDNConfig.Archives;
        if (archives == null) {
            Logger.Log($"Failed to load archives for {product} v{targetBuild.Version}");
            return;
        }

        var indexManifest = await IndexManifest.LoadAsync(targetBuild, targetBuild.TACTCDNConfig, loader); 
        var install = targetBuild.TACTBuildConfig.Install;
        if (install == null) {
            Logger.Log($"Failed to install {product} v{targetBuild.Version}");
            return;
        }

        if (install.Value.EKey == null) {
            Logger.Log($"BattleNGDP doesn't currently support CKey lookup for build config data.");
            return;
        }

        var installHash = install.Value.EKey.Value.ToString();
        using var installBuf = await loader.Load(targetBuild.TACTPath, "data", installHash, targetBuild.Armadillo);
        if (installBuf == null) {
            Logger.Log($"Failed to get install data {product} v{targetBuild.Version}");
            return;
        }
        
        var encoding = targetBuild.TACTBuildConfig.Encoding;
        if (encoding == null) {
            Logger.Log($"Failed to encoding {product} v{targetBuild.Version}");
            return;
        }

        if (encoding.Value.EKey == null) {
            Logger.Log($"BattleNGDP doesn't currently support CKey lookup for build config data.");
            return;
        }
        
        var encodingHash = encoding.Value.EKey.Value.ToString();
        using var encodingBuf = await loader.Load(targetBuild.TACTPath, "data", encodingHash, targetBuild.Armadillo);
        if (encodingBuf == null) {
            Logger.Log($"Failed to get encoding data {product} v{targetBuild.Version}");
            return;
        }
        
        var blte = new BLTE(encodingBuf.Value.ReadOnlySpan);
        var encodingManifest = new EncodingManifest(blte.Data);
        
        var installBlte = new BLTE(installBuf.Value.ReadOnlySpan);
        var installManifest = new InstallManifest(installBlte.Data);

        var validFiles = new List<InstallManifestFile>();
        Logger.Log($"");
        foreach (var file in  installManifest.Files) {
            var sizeStr = StringUtility.BytesToString(file.Size);
            
            Logger.Log($"{sizeStr,-14} {file.Name}");
            
            if (!encodingManifest.TryGetCKeyEntry(file.ContentKey.Span, out var ckeyEntry)) {
                Logger.Log($"  CKey: {Convert.ToHexStringLower(file.ContentKey.Span)} -> NOT FOUND IN ENCODING MANIFEST");
                continue;
            } 
            
            Logger.Log($"  CKey: {Convert.ToHexStringLower(file.ContentKey.Span)} -> {ckeyEntry.CKey.ToString().ToLowerInvariant()}");
            foreach (var eKey in encodingManifest.GetEKeys(ckeyEntry)) {
                if (encodingManifest.TryGetEKeyEntry(eKey, out var eKeyEntry)) {
                    Logger.Log($"  EKey: {eKeyEntry.EKey.ToString().ToLowerInvariant()} -> {eKeyEntry.FileSize} bytes ({eKeyEntry.ESpecIndex})");
                }
                
                if (!indexManifest.TryGetEntry(eKey, out var location)) { 
                    Logger.Log($"  EKey: {eKey} -> NOT FOUND IN CDN INDEX");
                    continue;
                }
                
                Logger.Log($"  EKey: {eKey}");
                Logger.Log($"    Archive: {location.Archive}");
                Logger.Log($"    Offset:  0x{location.Offset:X8}");
                Logger.Log($"    Size:    {location.Size}");
                Logger.Log($"    Type:    {location.Type}");
                
                validFiles.Add(file);
            }
        }
        
        Logger.Log($"Found {installManifest.Files.Length} files in install manifest for {product} v{targetBuild.Version}");
        Logger.Log($"");
        
        var targetFile = Prompt.Select("Select file to download", validFiles, textSelector:file => file.Name);
        

    }
}*/