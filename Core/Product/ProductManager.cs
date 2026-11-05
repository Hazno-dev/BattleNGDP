using BattleNGDP.BlizzTrack;
using BattleNGDP.TACT.Data;
using BattleNGDP.TACTChannel;

namespace BattleNGDP.Core;

public static class ProductManager
{
    public static async Task<ProductInfo> GetProduct(string tact)
    {
        var (versions, newVersionsCount) = await BlizzTrackAPI.GetManifests<BlizzTrackVersion>(tact);
        var (cdns, newcdnsCount) = await BlizzTrackAPI.GetManifests<BlizzTrackCDN>(tact);
        var productInfo = ProductStorage.GetTact(tact);
        var keyingSet = new HashSet<string>();

        Logger.Log($"BlizzTrack found {versions.Count} versions and {cdns.Count} cdns for {tact}");
        foreach (var version in versions) {
            productInfo.Name ??= version.Name;

            BlizzTrackManifest<BlizzTrackCDN>? versionCdn = cdns.FirstOrDefault(cdn => cdn.CreatedAt < version.CreatedAt);
            foreach (var versionData in version.Data) {
                var versionDataCdn = versionCdn?.Data?.FirstOrDefault(cdn => cdn.Region == versionData.Region, versionCdn.Value.Data[0]);
                if (newVersionsCount > 0 && !string.IsNullOrEmpty(versionData.Keyring) && keyingSet.Add(versionData.Keyring)) {
                    string? armadillo = null;
                    if (version.Encrypted) {
                        var tmp = new ProductBuildInfo(version, versionData, versionDataCdn, version.Encrypted);
                        await tmp.EnsureArmadillo();
                        armadillo = tmp.Armadillo;
                    }

                    await TACTKeyringService.ImportRemote(versionData.Keyring, versionDataCdn?.Path, version.Tact, armadillo);
                }

                if (!productInfo.ProductBuilds.TryGetValue(versionData.BuildId, out var productBuild)) {
                    productInfo.ProductBuilds.Add(versionData.BuildId,
                        [new ProductBuildInfo(version, versionData, versionDataCdn, version.Encrypted)]);
                    continue;
                }


                if (productBuild.Any(buildInfo => buildInfo == versionData)) {
                    continue;
                }

                productBuild.Add(new ProductBuildInfo(version, versionData, versionDataCdn, version.Encrypted));
            }
        }

        var (tactChannelsLatest, hasTactChanged) = await TACTChannelsService.GetLatest(tact);
        var firstTactBuild = tactChannelsLatest.Builds.Values.First();
        Logger.Log($"TactChannel latest found {firstTactBuild.Value.BuildKey}::{firstTactBuild.Value.CdnKey} at {firstTactBuild.Definition}");

        foreach (var build in tactChannelsLatest.Builds) {
            var buildData = build.Value.Value;
            if (hasTactChanged && !string.IsNullOrEmpty(buildData.KeyRing) && keyingSet.Add(buildData.KeyRing)) {
                var armadillo = buildData.ProductConfig.Data?["all"]?["config"]?["decryption_key_name"]?.GetValue<string?>();
                await TACTKeyringService.ImportRemote(buildData.KeyRing, buildData.Path, tactChannelsLatest.Variant, armadillo);
            }

            if (!productInfo.ProductBuilds.TryGetValue(buildData.VersionNumber, out var productBuild)) {
                productInfo.ProductBuilds.Add(buildData.VersionNumber,
                    [new ProductBuildInfo(buildData, build.Key, tactChannelsLatest.Variant, tactChannelsLatest.ObtainedDate)]);
                continue;
            }

            if (productBuild.Any(buildInfo => buildInfo == buildData)) {
                continue;
            }

            productBuild.Add(new ProductBuildInfo(buildData, build.Key, tactChannelsLatest.Variant, tactChannelsLatest.ObtainedDate));
        }

        ProductStorage.Save();
        return productInfo;
    }
}