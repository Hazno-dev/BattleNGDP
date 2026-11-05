using BattleNGDP.Core;
using BattleNGDP.TACT.CDN;

namespace BattleNGDP.TACT;

public class CDNConfig : TACTConfig
{
    public BuildConfigKeyValues? Archives;
    public BuildConfigSizeValues? ArchivesIndexSizes;
    public BuildConfigKeyValues? ArchiveGroup;
    public BuildConfigKeyValues? PatchArchives;
    public BuildConfigSizeValues? PatchArchivesIndexSizes;
    public BuildConfigKeyValues? PatchArchivesGroup;
    public BuildConfigKeyValues? FileIndex;

    public CDNConfig() : base() { }

    public CDNConfig(ReadOnlySpan<byte> data) : base(data)
    {
        Archives = BuildConfigKeyValues.Create(GetValue("archives"));
        ArchivesIndexSizes = BuildConfigSizeValues.Create(GetValue("archives-index-size"));
        ArchiveGroup = BuildConfigKeyValues.Create(GetValue("archives-group"));
        PatchArchives = BuildConfigKeyValues.Create(GetValue("patch-archives"));
        PatchArchivesIndexSizes = BuildConfigSizeValues.Create(GetValue("patch-archives-index-size"));
        PatchArchives = BuildConfigKeyValues.Create(GetValue("patch-archive-group"));
        FileIndex = BuildConfigKeyValues.Create(GetValue("file-index"));
    }

    public static async Task<CDNConfig?> LoadAsync(ProductBuildInfo buildInfo)
    {
        using var cdnConfigBuf = await TACTAssetService.GetConfig(buildInfo, buildInfo.TACTCDNConfigHash);
        if (cdnConfigBuf == null) {
            Logger.Log($"Failed to load cdn config for {buildInfo.TACTCDNConfigHash} v{buildInfo.Version}");
            return null;
        }

        return new CDNConfig(cdnConfigBuf.Value.ReadOnlySpan);
    }
}