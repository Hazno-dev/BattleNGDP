using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using BattleNGDP.CASC;
using BattleNGDP.Core;
using BattleNGDP.TACT.CDN;
using BattleNGDP.TACT.Data;

namespace BattleNGDP.TACT.Manifest;

public enum EntryType
{
    None,
    File,
    Link,
    Inline,
    Deleted,
    Unknown
}

[Flags]
public enum ManifestFlags : uint
{
    IncludeCKey = 0x01,
    WriteSupport = 0x02,
    PatchSupport = 0x04,
    LowercaseManifest = 0x08
}

[Flags]
public enum PathFlags
{
    SeparatorPre = 0x01,
    SeparatorPost = 0x02,
    NodeValue = 0x04
}

public readonly record struct PatchRecord(TACTKey EKey, uint ContentSize, TACTKey PatchKey, uint PatchSize, byte Age);

public readonly record struct ManifestSource(string Name, BuildConfigKeyValuePair Record, BuildConfigSizeValuePair? Size);

public readonly record struct FileSpan(uint Offset, uint Size, ContainerFile Container);

public sealed class Entry(string path, string sourceManifest, EntryType type, byte rawType, FileSpan[]? spans = null, byte[]? inlineData = null,
    string? linkTarget = null)
{
    public string Path { get; } = path;
    public string SourceManifest { get; } = sourceManifest;

    public EntryType Type { get; } = type;
    public byte RawType { get; } = rawType;

    public FileSpan[] Spans { get; } = spans ?? [];

    public byte[]? InlineData { get; } = inlineData;
    public string? LinkTarget { get; } = linkTarget;

    public Entry WithPath(string path) =>
        new(path, SourceManifest, Type, RawType, Spans, InlineData, LinkTarget);

    public override string ToString() =>
        $"{Path} ({Type}, {Size} bytes)";

    public ulong Size
    {
        get
        {
            if (Type == EntryType.Inline) {
                return (ulong) (InlineData?.Length ?? 0);
            }

            if (Type != EntryType.File) {
                return 0;
            }

            ulong size = 0;

            foreach (ref readonly var span in Spans.AsSpan()) {
                var end = (ulong) span.Offset + span.Size;
                if (end > size) {
                    size = end;
                }
            }

            return size;
        }
    }
}

public sealed class ContainerFile(TACTKey eKey, uint encodedSize, uint eSpecOffset, uint contentSize, TACTKey? cKey, PatchRecord[] patches)
{
    public TACTKey EKey { get; } = eKey;
    public TACTKey? CKey { get; } = cKey;

    public uint EncodedSize { get; } = encodedSize;
    public uint ESpecOffset { get; } = eSpecOffset;
    public uint ContentSize { get; } = contentSize;

    public PatchRecord[] Patches { get; } = patches;

    public override string ToString() =>
        $"{EKey} ({ContentSize} -> {EncodedSize})";
}

public sealed class TVFSManifest
{
    private readonly ref struct PathEntry(ReadOnlySpan<byte> name, PathFlags flags, uint nodeValue)
    {
        public readonly ReadOnlySpan<byte> Name = name;
        public readonly PathFlags Flags = flags;
        public readonly uint NodeValue = nodeValue;

        public bool HasNodeValue =>
            (Flags & PathFlags.NodeValue) != 0;
    }

    private sealed class ParsedManifest(string name, Header header, List<Entry> entries)
    {
        public string Name { get; } = name;
        public Header Header { get; } = header;

        public List<Entry> Entries { get; } = entries;
    }

    private readonly struct Header(byte version, byte headerSize,
        byte eKeySize, byte pKeySize,
        ManifestFlags flags,
        uint pathTableOffset, uint pathTableSize,
        uint vfsTableOffset, uint vfsTableSize,
        uint containerTableOffset, uint containerTableSize,
        ushort pathTableMaxDepth, uint eSpecTableOffset, uint eSpecTableSize)
    {
        public readonly byte Version = version;
        public readonly byte HeaderSize = headerSize;
        public readonly byte EKeySize = eKeySize;
        public readonly byte PKeySize = pKeySize;

        public readonly ManifestFlags Flags = flags;

        public readonly uint PathTableOffset = pathTableOffset;
        public readonly uint PathTableSize = pathTableSize;

        public readonly uint VFSTableOffset = vfsTableOffset;
        public readonly uint VFSTableSize = vfsTableSize;

        public readonly uint ContainerTableOffset = containerTableOffset;
        public readonly uint ContainerTableSize = containerTableSize;

        public readonly ushort PathTableMaxDepth = pathTableMaxDepth;

        public readonly uint ESpecTableOffset = eSpecTableOffset;
        public readonly uint ESpecTableSize = eSpecTableSize;

        public readonly int ContainerOffsetSize = GetOffsetSize(containerTableSize);
        public readonly int ESpecOffsetSize = GetOffsetSize(eSpecTableSize);
    }

    private const uint c_tvfs_Folder_Node = 0x80000000;
    private const uint c_tvfs_Folder_Size_Mask = 0x7FFFFFFF;

    private const byte c_entry_None = 0x00;
    private const byte c_entry_Link = 0xFD;
    private const byte c_entry_Inline = 0xFE;
    private const byte c_entry_Deleted = 0xFF;

    private readonly Dictionary<string, Entry> m_entries;

    private TVFSManifest(Dictionary<string, Entry> entries)
    {
        m_entries = entries;
    }

    public IReadOnlyDictionary<string, Entry> Entries =>
        m_entries;

    public int Count =>
        m_entries.Count;

    public Entry this[string path] =>
        m_entries[path];

    public bool TryGetFile(string path, out Entry entry) =>
        m_entries.TryGetValue(path, out entry!);


    private static async Task<ParsedManifest> LoadManifest(ProductBuildInfo buildInfo, ManifestSource source, IndexManifest indexManifest)
    {
        Debug.Assert(source.Record.EKey != null);

        Logger.Log($"    [*] Loading TVFS {source.Name} ...");
        var eKey = source.Record.EKey.Value.AsSpan();

        PooledBuffer<byte>? buffer;
        var eKeyString = source.Record.EKey.Value.ToString();
        if (!indexManifest.TryGetEntry(eKey[..], out var location)) {
            //Logger.Log($"    [*] Loading TVFS {source.Name} ...");
            Logger.Log($"[*]    TVFS manifest {source.Name} ({source.Record.EKey.Value.ToString()}) was not found in CDN indices.");
            Logger.Log("[*]     Attempting to load by ekey...");
            buffer = await TACTAssetService.GetDataBLTE(buildInfo, eKeyString);
        } else {
            buffer = location.Type switch {
                CDNSourceType.Loose => await TACTAssetService.GetDataBLTE(buildInfo, eKeyString),
                CDNSourceType.Archive => await TACTAssetService.GetArchiveBLTE(buildInfo,
                    location.Archive!,
                    new TACTArchiveBounds {
                        Offset = location.Offset,
                        Size = location.Size
                    }),
                _ => throw new InvalidDataException($"Unsupported CDN location for {source.Name}.")
            };
        }

        if (buffer == null) {
            throw new IOException($"Unable to load TVFS manifest {source.Name}.");
        }

        using var encodedBuffer = buffer.Value;
        if (source.Size is { CSize: > 0 } expected && encodedBuffer.Length != expected.CSize) {
            Logger.Log($"{source.Name}: decoded TVFS size mismatch: {encodedBuffer.Length} != {expected.CSize}");
        }

        return ParseManifest(source.Name, encodedBuffer.ReadOnlySpan);
    }

    private static async Task MountManifest(ProductBuildInfo targetBuild, ParsedManifest parsed, string sourceName, string prefix,
        Dictionary<string, Entry> output, Dictionary<TACTKey, ManifestSource> sourceLookup, Dictionary<string, ParsedManifest> parsedCache,
        HashSet<string> recursionStack, IndexManifest indexManifest)
    {
        if (!recursionStack.Add(sourceName)) {
            throw new InvalidDataException($"Recursive TVFS mount detected at {sourceName}.");
        }

        try {
            foreach (var localEntry in parsed.Entries) {
                var path = prefix.Length == 0 ? localEntry.Path : prefix + localEntry.Path;

                if (TryGetMountedManifest(localEntry, sourceLookup, out var childSource)) {
                    var mountPath = path.EndsWith(':') ? path : path + ":";

                    output[mountPath] = localEntry.WithPath(mountPath);
                    if (!parsedCache.TryGetValue(childSource.Name, out var child)) {
                        child = await LoadManifest(targetBuild, childSource, indexManifest);
                        parsedCache[childSource.Name] = child;
                    }

                    await MountManifest(targetBuild, child, childSource.Name, mountPath, output, sourceLookup, parsedCache, recursionStack,
                        indexManifest);
                    continue;
                }

                output[path] = localEntry.WithPath(path);
            }
        } finally {
            recursionStack.Remove(sourceName);
        }
    }

    private static bool TryGetMountedManifest(Entry entry, Dictionary<TACTKey, ManifestSource> sourceLookup, out ManifestSource source)
    {
        if (entry is { Type: EntryType.File, Spans.Length: 1 }) {
            return sourceLookup.TryGetValue(entry.Spans[0].Container.EKey, out source);
        }

        source = default;
        return false;
    }

    private static ParsedManifest ParseManifest(string sourceName, ReadOnlySpan<byte> data)
    {
        var header = ReadHeader(data);
        var pathTable = SliceTable(data, header.PathTableOffset, header.PathTableSize, "path");
        var vfsTable = SliceTable(data, header.VFSTableOffset, header.VFSTableSize, "VFS");
        var containerTable = SliceTable(data, header.ContainerTableOffset, header.ContainerTableSize, "container");
        var eSpecTable = header.ESpecTableSize == 0
            ? default
            : SliceTable(data, header.ESpecTableOffset, header.ESpecTableSize, "ESpec");

        var entries = new List<Entry>();
        var containerCache = new Dictionary<uint, ContainerFile>();
        var pathStart = 0;
        var pathLength = pathTable.Length;
        if (pathTable.Length >= 5 && pathTable[0] == 0xFF) {
            var nodeValue = BinaryPrimitives.ReadUInt32BigEndian(pathTable.Slice(1, 4));
            if ((nodeValue & c_tvfs_Folder_Node) == 0) {
                throw new InvalidDataException($"{sourceName}: root TVFS node is not a folder.");
            }

            var folderSize = nodeValue & c_tvfs_Folder_Size_Mask;
            if (folderSize < sizeof(uint)) {
                throw new InvalidDataException($"{sourceName}: invalid root TVFS folder size.");
            }

            pathStart = 1 + sizeof(uint);
            pathLength = checked((int) folderSize - sizeof(uint));
            if (pathStart + pathLength > pathTable.Length) {
                throw new InvalidDataException($"{sourceName}: root path node exceeds path table.");
            }
        }

        var path = new StringBuilder(header.PathTableMaxDepth * 16);
        ParsePathRange(sourceName, pathTable.Slice(pathStart, pathLength), path, entries, vfsTable, containerTable, eSpecTable, containerCache,
            in header);
        return new ParsedManifest(sourceName, header, entries);
    }

    private static Header ReadHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x26) {
            throw new InvalidDataException("TVFS header is truncated.");
        }

        if (!data[..4].SequenceEqual("TVFS"u8)) {
            throw new InvalidDataException("Invalid TVFS magic.");
        }

        var version = data[4];
        if (version != 1) {
            throw new InvalidDataException($"Unsupported TVFS version {version}.");
        }

        var headerSize = data[5];
        if (headerSize < 0x26 || headerSize > data.Length) {
            throw new InvalidDataException($"Invalid TVFS header size {headerSize}.");
        }

        var eKeySize = data[6];
        var pKeySize = data[7];

        if (eKeySize is not 9 and not 16) {
            throw new InvalidDataException($"Unsupported TVFS EKey size {eKeySize}.");
        }

        if (pKeySize != eKeySize) {
            throw new InvalidDataException($"TVFS PKey size {pKeySize} does not match EKey size {eKeySize}.");
        }

        var flags = (ManifestFlags) BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0x08, 4));
        var pathOffset = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0x0C, 4));
        var pathSize = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0x10, 4));
        var vfsOffset = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0x14, 4));
        var vfsSize = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0x18, 4));
        var containerOffset = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0x1C, 4));
        var containerSize = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0x20, 4));
        var maxDepth = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(0x24, 2));

        uint eSpecOffset = 0;
        uint eSpecSize = 0;

        if ((flags & ManifestFlags.WriteSupport) != 0) {
            if (headerSize < 0x2E ||
                data.Length < 0x2E) {
                throw new InvalidDataException("TVFS write-support header is truncated.");
            }

            eSpecOffset = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0x26, 4));
            eSpecSize = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0x2A, 4));
        }

        return new Header(version, headerSize, eKeySize, pKeySize, flags, pathOffset, pathSize, vfsOffset, vfsSize, containerOffset, containerSize,
            maxDepth, eSpecOffset, eSpecSize);
    }

    private static void ParsePathRange(string sourceName, ReadOnlySpan<byte> directory, StringBuilder path, List<Entry> entries,
        ReadOnlySpan<byte> vfsTable, ReadOnlySpan<byte> containerTable, ReadOnlySpan<byte> eSpecTable, Dictionary<uint, ContainerFile> containerCache,
        in Header header)
    {
        var pos = 0;
        var directoryPathLength = path.Length;

        while (pos < directory.Length) {
            var pathEntry = ReadPathEntry(directory, ref pos);
            if ((pathEntry.Flags & PathFlags.SeparatorPre) != 0) {
                path.Append('/');
            }

            if (!pathEntry.Name.IsEmpty) {
                path.Append(Encoding.UTF8.GetString(pathEntry.Name));
            }

            if ((pathEntry.Flags & PathFlags.SeparatorPost) != 0) {
                path.Append('/');
            }

            if (!pathEntry.HasNodeValue) {
                continue;
            }

            var nodeValue = pathEntry.NodeValue;
            if ((nodeValue & c_tvfs_Folder_Node) != 0) {
                var folderSize = nodeValue & c_tvfs_Folder_Size_Mask;
                if (folderSize < sizeof(uint)) {
                    throw new InvalidDataException($"{sourceName}: invalid folder node size.");
                }

                var childSize = checked((int) folderSize - sizeof(uint));

                if (pos + childSize > directory.Length) {
                    throw new InvalidDataException($"{sourceName}: folder node exceeds path table.");
                }

                ParsePathRange(sourceName, directory.Slice(pos, childSize), path, entries, vfsTable, containerTable, eSpecTable, containerCache,
                    in header);
                pos += childSize;
            } else {
                var entry = ReadVFSFile(sourceName, path.ToString(), nodeValue, vfsTable, containerTable, eSpecTable, containerCache, in header);
                entries.Add(entry);
            }

            path.Length = directoryPathLength;
        }
    }

    private static PathEntry ReadPathEntry(ReadOnlySpan<byte> directory, ref int pos)
    {
        PathFlags flags = 0;
        uint nodeValue = 0;
        ReadOnlySpan<byte> name = default;

        if (pos < directory.Length && directory[pos] == 0x00) {
            flags |= PathFlags.SeparatorPre;
            pos++;
        }

        if (pos < directory.Length && directory[pos] != 0xFF) {
            int nameLength = directory[pos++];
            EnsureAvailable(directory, pos, nameLength);
            name = directory.Slice(pos, nameLength);
            pos += nameLength;
        }

        if (pos < directory.Length && directory[pos] == 0x00) {
            flags |= PathFlags.SeparatorPost;
            pos++;
        }

        if (pos < directory.Length) {
            if (directory[pos] == 0xFF) {
                EnsureAvailable(directory, pos, 5);

                nodeValue = BinaryPrimitives.ReadUInt32BigEndian(directory.Slice(pos + 1, 4));
                flags |= PathFlags.NodeValue;
                pos += 5;
            } else {
                //dont consume
                flags |= PathFlags.SeparatorPost;
            }
        }

        return new PathEntry(name, flags, nodeValue);
    }

    private static Entry ReadVFSFile(string sourceName, string path, uint vfsOffset, ReadOnlySpan<byte> vfsTable, ReadOnlySpan<byte> containerTable,
        ReadOnlySpan<byte> eSpecTable, Dictionary<uint, ContainerFile> containerCache, in Header header)
    {
        if (vfsOffset >= (uint) vfsTable.Length) {
            throw new InvalidDataException($"{sourceName}: VFS offset 0x{vfsOffset:X} is outside the VFS table.");
        }

        var pos = checked((int) vfsOffset);
        var type = vfsTable[pos++];

        if (type == c_entry_None) {
            return new Entry(path, sourceName, EntryType.None, type);
        }

        //casclib considers 1..224 / 0x01..0xE0 valid
        if (type is >= 0x01 and <= 0xE0) {
            if (header.ContainerOffsetSize == 0) {
                throw new InvalidDataException($"{sourceName}: file has spans but container table is empty.");
            }

            var spans = new FileSpan[type];
            for (var i = 0; i < spans.Length; i++) {
                var fileOffset = ReadUInt32BE(vfsTable, ref pos);
                var size = ReadUInt32BE(vfsTable, ref pos);
                var containerOffset = ReadVariableUIntBE(vfsTable, ref pos, header.ContainerOffsetSize);
                if (!containerCache.TryGetValue(containerOffset, out var container)) {
                    container = ReadContainerFile(sourceName, containerOffset, containerTable, eSpecTable, in header);
                    containerCache[containerOffset] = container;
                }

                spans[i] = new FileSpan(fileOffset, size, container);
            }

            return new Entry(path, sourceName, EntryType.File, type, spans);
        }

        if (type == c_entry_Inline) {
            EnsureAvailable(vfsTable, pos, 1);

            var size = vfsTable[pos++] + 1;
            EnsureAvailable(vfsTable, pos, size);
            var inlineData = vfsTable.Slice(pos, size).ToArray();
            return new Entry(path, sourceName, EntryType.Inline, type, inlineData: inlineData);
        }

        if (type == c_entry_Link) {
            var target = new StringBuilder();
            var first = true;
            while (true) {
                EnsureAvailable(vfsTable, pos, 1);

                var componentLength = unchecked((sbyte) vfsTable[pos++]);
                if (componentLength == -1) {
                    break;
                }

                if (componentLength < 0) {
                    throw new InvalidDataException($"{sourceName}: invalid TVFS link component size.");
                }

                EnsureAvailable(vfsTable, pos, componentLength);

                if (!first) {
                    target.Append('/');
                }

                target.Append(Encoding.UTF8.GetString(vfsTable.Slice(pos, componentLength)));
                pos += componentLength;
                first = false;
            }

            return new Entry(path, sourceName, EntryType.Link, type, linkTarget: target.ToString());
        }

        if (type == c_entry_Deleted) {
            return new Entry(path, sourceName, EntryType.Deleted, type);
        }

        return new Entry(path, sourceName, EntryType.Unknown, type);
    }

    private static ContainerFile ReadContainerFile(string sourceName, uint offset, ReadOnlySpan<byte> containerTable, ReadOnlySpan<byte> eSpecTable,
        in Header header)
    {
        if (offset >= (uint) containerTable.Length) {
            throw new InvalidDataException($"{sourceName}: CFT offset 0x{offset:X} is outside the container table.");
        }

        var pos = checked((int) offset);
        EnsureAvailable(containerTable, pos, header.EKeySize);

        var eKey = new TACTKey(containerTable.Slice(pos, header.EKeySize));
        pos += header.EKeySize;

        var encodedSize = ReadUInt32BE(containerTable, ref pos);
        var eSpecOffset = ReadVariableUIntBE(containerTable, ref pos, header.ESpecOffsetSize);
        var contentSize = ReadUInt32BE(containerTable, ref pos);

        TACTKey? cKey = null;
        if ((header.Flags & ManifestFlags.IncludeCKey) != 0) {
            EnsureAvailable(containerTable, pos, 16);
            cKey = new TACTKey(containerTable.Slice(pos, 16));
            pos += 16;
        }

        var patches = Array.Empty<PatchRecord>();
        if ((header.Flags & ManifestFlags.PatchSupport) != 0) {
            EnsureAvailable(containerTable, pos, 1);

            int patchCount = containerTable[pos++];
            patches = new PatchRecord[patchCount];
            for (var i = 0; i < patchCount; i++) {
                EnsureAvailable(containerTable, pos, header.EKeySize);

                var patchEKey = new TACTKey(containerTable.Slice(pos, header.EKeySize));
                pos += header.EKeySize;

                var patchContentSize = ReadUInt32BE(containerTable, ref pos);
                EnsureAvailable(containerTable, pos, header.PKeySize);

                var patchKey = new TACTKey(containerTable.Slice(pos, header.PKeySize));
                pos += header.PKeySize;

                var patchSize = ReadUInt32BE(containerTable, ref pos);
                EnsureAvailable(containerTable, pos, 1);

                var age = containerTable[pos++];
                patches[i] = new PatchRecord(patchEKey, patchContentSize, patchKey, patchSize, age);
            }
        }

        return new ContainerFile(eKey, encodedSize, eSpecOffset, contentSize, cKey, patches);
    }

    private static int GetOffsetSize(uint tableSize) =>
        tableSize switch {
            0 => 0,
            <= 0xFF => 1,
            <= 0xFFFF => 2,
            <= 0xFFFFFF => 3,
            _ => 4
        };

    private static uint ReadUInt32BE(ReadOnlySpan<byte> data, ref int pos)
    {
        EnsureAvailable(data, pos, sizeof(uint));
        var value = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos, sizeof(uint)));
        pos += sizeof(uint);
        return value;
    }

    private static uint ReadVariableUIntBE(ReadOnlySpan<byte> data, ref int pos, int byteCount)
    {
        if (byteCount == 0) {
            return 0;
        }

        if (byteCount is < 0 or > 4) {
            throw new ArgumentOutOfRangeException(nameof(byteCount));
        }

        EnsureAvailable(data, pos, byteCount);

        uint value = 0;
        for (var i = 0; i < byteCount; i++) {
            value = (value << 8) | data[pos++];
        }

        return value;
    }

    private static ReadOnlySpan<byte> SliceTable(ReadOnlySpan<byte> data, uint offset, uint size, string name)
    {
        var end = (ulong) offset + size;
        if (end > (ulong) data.Length) {
            throw new InvalidDataException($"TVFS {name} table exceeds manifest data.");
        }

        return data.Slice(checked((int) offset), checked((int) size));
    }

    private static void EnsureAvailable(ReadOnlySpan<byte> data, int offset, int size)
    {
        if (offset < 0 || size < 0 || offset > data.Length - size) {
            throw new InvalidDataException("Unexpected end of TVFS data.");
        }
    }

    public static async Task<TVFSManifest> LoadAsync(ProductBuildInfo targetBuild, IndexManifest indexManifest)
    {
        if (targetBuild.TACTBuildConfig is null) {
            throw new InvalidDataException("Target build has no loaded config.");
        }

        if (targetBuild.TACTBuildConfig.VFSRoot is not { } rootRecord) {
            throw new InvalidDataException("Build config does not contain vfs-root.");
        }

        var rootSource = new ManifestSource("vfs-root", rootRecord, targetBuild.TACTBuildConfig.VFSRootSize);
        var childSources = new List<ManifestSource>(targetBuild.TACTBuildConfig.VFSManifests?.Length ?? 0);

        if (targetBuild.TACTBuildConfig.VFSManifests is { } manifests) {
            for (var i = 0; i < manifests.Length; i++) {
                BuildConfigSizeValuePair? size = null;

                if (targetBuild.TACTBuildConfig.VFSManifestsSizes is { } sizes && i < sizes.Length) {
                    size = sizes[i];
                }

                childSources.Add(new ManifestSource($"vfs-{i + 1}", manifests[i], size));
            }
        }

        //Support for both 9 and 16 byte keys
        var sourceLookup = new Dictionary<TACTKey, ManifestSource>();
        foreach (var source in childSources) {
            var eKey = source.Record.EKey.Value.AsSpan();
            var full = new TACTKey(eKey[..]);
            var truncated = new TACTKey(eKey[..9]);

            sourceLookup[full] = source;
            sourceLookup[truncated] = source;
        }

        var parsedCache = new Dictionary<string, ParsedManifest>(StringComparer.Ordinal);
        var root = await LoadManifest(targetBuild, rootSource, indexManifest);

        parsedCache[rootSource.Name] = root;
        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var recursionStack = new HashSet<string>(StringComparer.Ordinal);

        await MountManifest(targetBuild, root, rootSource.Name, "", entries, sourceLookup, parsedCache, recursionStack, indexManifest);
        return new TVFSManifest(entries);
    }
}