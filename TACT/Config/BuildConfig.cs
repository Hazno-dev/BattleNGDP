using BattleNGDP.CASC;
using BattleNGDP.Core;
using BattleNGDP.TACT.CDN;

namespace BattleNGDP.TACT;

public class BuildConfig : TACTConfig
{
    public readonly BuildConfigKeyValuePair? Root;
    public readonly BuildConfigKeyValuePair? Install;
    public readonly BuildConfigSizeValuePair? InstallSize;
    public readonly BuildConfigKeyValuePair? Download;
    public readonly BuildConfigSizeValuePair? DownloadSize;
    public readonly BuildConfigKeyValuePair? Size;
    public readonly BuildConfigSizeValuePair? SizeSize;
    public readonly BuildConfigKeyValuePair? Encoding;
    public readonly BuildConfigSizeValuePair? EncodingSize;
    public readonly BuildConfigKeyValuePair? PatchIndex;
    public readonly BuildConfigSizeValuePair? PatchIndexSize;
    public readonly BuildConfigKeyValuePair? Patch;
    public readonly BuildConfigSizeValuePair? PatchSize;
    public readonly BuildConfigKeyValuePair? VFSRoot;
    public readonly BuildConfigSizeValuePair? VFSRootSize;
    public readonly BuildConfigKeyValuePair[]? VFSManifests;
    public readonly BuildConfigSizeValuePair[]? VFSManifestsSizes;
    public readonly BuildConfigKeyValuePair? BuildFileDB;
    public readonly BuildConfigSizeValuePair? BuildFileDBSize;

    public string? Name;
    public string? Comments;
    public string? Branch;
    public string? Creator;
    public string? Product;
    public string? Uid;
    public string? ClientVersion;

    public BuildConfig() : base() { }

    public BuildConfig(ReadOnlySpan<byte> data) : base(data)
    {
        Root = BuildConfigKeyValuePair.Create(GetValue("root"));
        Install = BuildConfigKeyValuePair.Create(GetValue("install"));
        InstallSize = BuildConfigSizeValuePair.Create(GetValue("install-size"));
        Download = BuildConfigKeyValuePair.Create(GetValue("download"));
        DownloadSize = BuildConfigSizeValuePair.Create(GetValue("download-size"));
        Size = BuildConfigKeyValuePair.Create(GetValue("size"));
        SizeSize = BuildConfigSizeValuePair.Create(GetValue("size-size"));
        Encoding = BuildConfigKeyValuePair.Create(GetValue("encoding"));
        EncodingSize = BuildConfigSizeValuePair.Create(GetValue("encoding-size"));
        PatchIndex = BuildConfigKeyValuePair.Create(GetValue("patch-index"));
        PatchIndexSize = BuildConfigSizeValuePair.Create(GetValue("patch-index-size"));
        Patch = BuildConfigKeyValuePair.Create(GetValue("patch"));
        PatchSize = BuildConfigSizeValuePair.Create(GetValue("patch-size"));
        VFSRoot = BuildConfigKeyValuePair.Create(GetValue("vfs-root"));
        VFSRootSize = BuildConfigSizeValuePair.Create(GetValue("vfs-root-size"));
        VFSManifests = BuildConfigKeyValuePair.CreateMulti(GetValues("vfs-{0}", 1));
        VFSManifestsSizes = BuildConfigSizeValuePair.CreateMulti(GetValues("vfs-{0}-size", 1));
        BuildFileDB = BuildConfigKeyValuePair.Create(GetValue("build-file-db"));
        BuildFileDBSize = BuildConfigSizeValuePair.Create(GetValue("build-file-db-size"));

        Name = GetValue("build-name");
        Comments = GetValue("build-comments");
        Branch = GetValue("build-branch");
        Creator = GetValue("build-creator");
        Product = GetValue("build-product");
        Uid = GetValue("build-uid");

        ClientVersion = GetValue("client-version");
    }

    public static async Task<BuildConfig?> LoadAsync(ProductBuildInfo buildInfo)
    {
        using var buildConfigBuf = await TACTAssetService.GetConfig(buildInfo, buildInfo.TACTBuildConfigHash);
        if (buildConfigBuf == null) {
            Logger.Log($"Failed to load build config for {buildInfo.TACTBuildConfig} v{buildInfo.Version}");
            return null;
        }

        return new BuildConfig(buildConfigBuf.Value.ReadOnlySpan);
    }
}