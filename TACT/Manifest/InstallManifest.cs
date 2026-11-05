using System.Text.Json.Serialization;
using BattleNGDP.CASC;
using BattleNGDP.Core;
using BattleNGDP.TACT.CDN;

namespace BattleNGDP.TACT.Manifest;

public sealed class InstallTag
{
    public byte[] AllFiles { get; set; }

    public string Name { get; set; }

    public ushort Type { get; set; }

    [JsonIgnore]
    public ReadOnlyMemory<byte> Files => AllFiles;

    public int FileCount { get; set; }

    public InstallTag()
    {
        Name = string.Empty;
        Type = 0;
        FileCount = 0;
        AllFiles = Array.Empty<byte>();
    }

    public InstallTag(string name, ushort type, byte[] files, int fileCount)
    {
        Name = name;
        Type = type;
        AllFiles = files;
        FileCount = fileCount;
    }

    public bool ContainsFile(int index)
    {
        if ((uint) index >= (uint) FileCount) {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var byteIndex = index >> 3;
        var bitIndex = index & 7;
        return (AllFiles[byteIndex] & (0x80 >> bitIndex)) != 0;
    }
}

public sealed class InstallManifestFile(string name, byte[] contentKey, uint size)
{
    public byte[] CKey { get; set; } = contentKey;
    public string Name { get; set; } = name;
    public uint Size { get; set; } = size;

    public InstallManifestFile() : this(string.Empty, [], 0) { }
}

/// <summary>
/// Lists the files installed on disk.
/// </summary>
public sealed class InstallManifest
{
    public byte Version { get; set; }
    public byte HashSize { get; set; }
    public InstallTag[] Tags { get; set; }
    public InstallManifestFile[] Files { get; set; }

    public InstallManifest()
    {
        Version = 0;
        HashSize = 0;
        Tags = [];
        Files = [];
    }

    public InstallManifest(ReadOnlySpan<byte> data)
    {
        var reader = new SpanReader(data);

        if (!reader.ReadBytes(2).SequenceEqual("IN"u8)) {
            throw new InvalidDataException("Invalid install manifest signature.");
        }

        Version = reader.ReadByte();
        HashSize = reader.ReadByte();

        var tagCount = reader.ReadUInt16BE();
        var fileCountRaw = reader.ReadUInt32BE();

        if (fileCountRaw > int.MaxValue) {
            throw new NotSupportedException("Install manifest contains too many files.");
        }

        var ckeySize = HashSize;
        if (Version >= 2) {
            ckeySize = reader.ReadByte();
            reader.ReadUInt32BE(); //entry count 2?
            reader.ReadByte(); //unk
        }

        var fileCount = (int) fileCountRaw;

        //ceil(fileCount / 8)
        var tagBitfieldSize = checked((int) (((long) fileCount + 7) / 8));

        Tags = new InstallTag[tagCount];

        for (var i = 0; i < Tags.Length; i++) {
            var name = reader.ReadCString();
            var type = reader.ReadUInt16BE();
            var files = reader.ReadBytes(tagBitfieldSize).ToArray();
            Tags[i] = new InstallTag(name, type, files, fileCount);
        }

        Files = new InstallManifestFile[fileCount];

        for (var i = 0; i < Files.Length; i++) {
            var name = reader.ReadCString();
            var contentKey = reader.ReadBytes(ckeySize).ToArray();
            var size = reader.ReadUInt32BE();
            Files[i] = new InstallManifestFile(name, contentKey, size);
        }

        if (reader.Remaining != 0) {
            Logger.Log($"Install manifest has {reader.Remaining} trailing bytes.");
            var base64Data = Convert.ToHexString(data);
            throw new InvalidDataException($"Install manifest has invalid data. " +
                                           $"Version {Version} - HashSize {HashSize} - " +
                                           $"TagCount {tagCount} - FileCount {fileCountRaw} - " +
                                           $"Data: {base64Data}");
        }
    }

    public static async Task<InstallManifest?> LoadAsync(ProductBuildInfo buildInfo)
    {
        var install = buildInfo.TACTBuildConfig?.Install;
        if (install?.EKey == null) {
            return null;
        }

        var installHash = install.Value.EKey.Value.ToString();
        using var installBuf = await TACTAssetService.GetDataBLTE(buildInfo, installHash);
        if (installBuf == null) {
            throw new NullReferenceException($"Failed to get install {installHash} from CDNs for {buildInfo.TACTName} v{buildInfo.Version}");
        }

        return new InstallManifest(installBuf.Value.Span);
    }
}