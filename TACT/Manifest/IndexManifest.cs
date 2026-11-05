using System.Buffers.Binary;
using System.Diagnostics;
using BattleNGDP.Core;
using BattleNGDP.TACT.CDN;
using BattleNGDP.TACT.Data;

namespace BattleNGDP.TACT.Manifest;

public enum CDNIndexType
{
    Loose,
    Archive,
    Group,
    PatchGroup,
    Unknown
}

public enum CDNSourceType
{
    Loose,
    Archive
}

/// <summary>
/// Locator for data on blizzard CDNs
/// </summary>
public readonly struct CDNFileLocation
{
    public readonly TACTKey EKey;
    public readonly CDNSourceType Type;
    public readonly string? Archive;

    public readonly ulong Offset;
    public readonly ulong Size;

    public CDNFileLocation(TACTKey eKey, ulong size)
    {
        Type = CDNSourceType.Loose;

        EKey = eKey;
        Archive = null;
        Offset = 0;
        Size = size;
    }

    public CDNFileLocation(TACTKey eKey, string archive, ulong offset, ulong size)
    {
        Type = CDNSourceType.Archive;

        EKey = eKey;
        Archive = archive;
        Offset = offset;
        Size = size;
    }
}

public readonly struct CDNIndexEntry(TACTKey eKey, ulong size, ulong offset, ushort archiveIndex = 0)
{
    public TACTKey EKey { get; } = eKey;

    public ulong Size { get; } = size;

    ///Offset for archive entries. Zero for loose entries.
    public ulong Offset { get; } = offset;

    public ushort ArchiveIndex { get; } = archiveIndex;
}

public sealed class CDNIndex
{
    private readonly CDNIndexEntry[] m_entries;

    public byte FormatRevision { get; }
    public byte Flags0 { get; }
    public byte Flags1 { get; }

    public int BlockSize { get; }

    public byte OffsetBytes { get; }
    public byte SizeBytes { get; }
    public byte KeyBytes { get; }
    public byte HashBytes { get; }

    public uint ElementCount { get; }

    public CDNIndexType Type =>
        OffsetBytes switch {
            0 => CDNIndexType.Loose,
            4 => CDNIndexType.Archive,
            5 => CDNIndexType.PatchGroup,
            6 => CDNIndexType.Group,
            _ => CDNIndexType.Unknown
        };

    public int Count => m_entries.Length;

    public ref readonly CDNIndexEntry this[int index] =>
        ref m_entries[index];

    public ReadOnlySpan<CDNIndexEntry> Entries =>
        m_entries;

    public CDNIndex(ReadOnlySpan<byte> data)
    {
        if (data.Length < 14) {
            throw new InvalidDataException("CDN index is too small.");
        }

        var footer = FindFooter(data, out var footerOffset);
        FormatRevision = footer[0];
        Flags0 = footer[1];
        Flags1 = footer[2];

        var blockSizeKB = footer[3];

        OffsetBytes = footer[4];
        SizeBytes = footer[5];
        KeyBytes = footer[6];
        HashBytes = footer[7];

        if (FormatRevision != 1) {
            throw new NotSupportedException($"Unsupported CDN index revision {FormatRevision}.");
        }

        if (KeyBytes is 0 or > 16) {
            throw new InvalidDataException($"Invalid CDN index key size {KeyBytes}.");
        }

        if (SizeBytes is 0 or > 8) {
            throw new InvalidDataException($"Invalid CDN index size field width {SizeBytes}.");
        }

        if (OffsetBytes > 8) {
            throw new InvalidDataException($"Invalid CDN index offset field width {OffsetBytes}.");
        }

        BlockSize = blockSizeKB << 10;

        if (BlockSize == 0) {
            throw new InvalidDataException("Invalid CDN index block size.");
        }

        var countLE = BinaryPrimitives.ReadUInt32LittleEndian(footer.Slice(8, 4));
        var countBE = BinaryPrimitives.ReadUInt32BigEndian(footer.Slice(8, 4));
        ElementCount = ChooseElementCount(data.Length, countLE, countBE);

        var entrySize = checked(KeyBytes + SizeBytes + OffsetBytes);
        var entriesPerBlock = BlockSize / entrySize;
        if (entriesPerBlock == 0) {
            throw new InvalidDataException("CDN index entry exceeds block size.");
        }

        var elementCount = checked((int) ElementCount);
        var blockCount = elementCount == 0 ? 0 : (elementCount + entriesPerBlock - 1) / entriesPerBlock;
        var dataBlockSize = checked(blockCount * BlockSize);

        var expectedSize = checked(dataBlockSize + blockCount * KeyBytes + blockCount * HashBytes + HashBytes + 12 + HashBytes);

        if (expectedSize != data.Length) {
            throw new InvalidDataException($"Invalid CDN index layout... Expected {expectedSize} bytes from footer metadata but got {data.Length}.");
        }

        m_entries = new CDNIndexEntry[elementCount];

        var outputIndex = 0;
        for (var blockIndex = 0; blockIndex < blockCount; blockIndex++) {
            var block = data.Slice(blockIndex * BlockSize, BlockSize);
            var remaining = elementCount - outputIndex;
            var entriesInBlock = Math.Min(entriesPerBlock, remaining);

            for (var i = 0; i < entriesInBlock; i++) {
                var rawEntry = block.Slice(i * entrySize, entrySize);

                var keyBytes = rawEntry[..KeyBytes];

                if (IsZero(keyBytes)) {
                    throw new InvalidDataException($"Unexpected empty EKey in block {blockIndex}, entry {i}.");
                }

                var eKey = new TACTKey(keyBytes);
                int position = KeyBytes;
                var size = ReadUIntBE(rawEntry.Slice(position, SizeBytes));

                position += SizeBytes;
                ulong offset = 0;
                ushort archiveIndex = 0;

                switch (Type) {
                    case CDNIndexType.Loose: {
                        //no offset :D
                        break;
                    }

                    case CDNIndexType.Archive: {
                        offset = ReadUIntBE(rawEntry.Slice(position, OffsetBytes));
                        break;
                    }

                    case CDNIndexType.Group: {
                        //6 byte offset u16 archiveIndex u32 archiveOffset
                        archiveIndex = BinaryPrimitives.ReadUInt16BigEndian(rawEntry.Slice(position, 2));
                        offset = BinaryPrimitives.ReadUInt32BigEndian(rawEntry.Slice(position + 2, 4));
                        break;
                    }

                    default: {
                        //unk
                        offset = ReadUIntBE(rawEntry.Slice(position, OffsetBytes));
                        break;
                    }
                }

                m_entries[outputIndex++] = new CDNIndexEntry(eKey, size, offset, archiveIndex);
            }
        }
    }

    public bool TryGetEntry(TACTKey eKey, out CDNIndexEntry entry)
    {
        var low = 0;
        var high = m_entries.Length - 1;

        while (low <= high) {
            var middle = low + ((high - low) >> 1);

            ref readonly var candidate = ref m_entries[middle];
            var comparison = candidate.EKey.CompareSequential(eKey);

            if (comparison == 0) {
                entry = candidate;
                return true;
            }

            if (comparison < 0) {
                low = middle + 1;
            } else {
                high = middle - 1;
            }
        }

        entry = default;
        return false;
    }

    public bool TryGetEntry(ReadOnlySpan<byte> eKey, out CDNIndexEntry entry)
    {
        if (eKey.Length != KeyBytes) {
            entry = default;
            return false;
        }

        return TryGetEntry(new TACTKey(eKey), out entry);
    }

    private static ReadOnlySpan<byte> FindFooter(ReadOnlySpan<byte> data, out int offset)
    {
        for (var hashBytes = 1; hashBytes <= 16; hashBytes++) {
            var footerSize = 12 + hashBytes;
            if (data.Length < footerSize) {
                continue;
            }

            var candidateOffset = data.Length - footerSize;
            var candidate = data[candidateOffset..];
            if (candidate[7] != hashBytes) {
                continue;
            }

            if (candidate[0] != 1) {
                continue;
            }

            if (candidate[3] == 0) {
                continue;
            }

            if (candidate[5] == 0) {
                continue;
            }

            if (candidate[6] is 0 or > 16) {
                continue;
            }

            offset = candidateOffset;
            return candidate;
        }

        throw new InvalidDataException("Could not locate CDN index footer.");
    }

    private uint ChooseElementCount(int fileSize, uint littleEndian, uint bigEndian)
    {
        var littleValid = IsValidElementCount(fileSize, littleEndian);
        var bigValid = IsValidElementCount(fileSize, bigEndian);

        if (littleValid && !bigValid) {
            return littleEndian;
        }

        if (bigValid && !littleValid) {
            return bigEndian;
        }

        if (littleValid) {
            return littleEndian;
        }

        throw new InvalidDataException($"Neither le({littleEndian}) or be({bigEndian}) element count produces a valid index layout.");
    }

    private bool IsValidElementCount(int fileSize, uint elementCount)
    {
        var entrySize = KeyBytes + SizeBytes + OffsetBytes;
        var entriesPerBlock = BlockSize / entrySize;
        if (entriesPerBlock == 0) {
            return false;
        }

        var blockCount = elementCount == 0 ? 0 : (elementCount + (ulong) entriesPerBlock - 1) / (ulong) entriesPerBlock;

        var expectedSize = blockCount * (ulong) BlockSize + blockCount * KeyBytes + blockCount * HashBytes + HashBytes + 12UL + HashBytes;
        return expectedSize == (ulong) fileSize;
    }

    private static ulong ReadUIntBE(ReadOnlySpan<byte> data)
    {
        if (data.Length > sizeof(ulong)) {
            throw new ArgumentOutOfRangeException(nameof(data));
        }

        ulong value = 0;
        foreach (var b in data) {
            value = (value << 8) | b;
        }

        return value;
    }

    private static bool IsZero(ReadOnlySpan<byte> data)
    {
        foreach (var b in data) {
            if (b != 0) {
                return false;
            }
        }

        return true;
    }
}

public sealed class IndexManifest
{
    public readonly record struct IndexSource(string Hash, CDNSourceType Type);

    private readonly Dictionary<TACTKey, CDNFileLocation> m_entries;

    public int Count => m_entries.Count;

    public bool TryGetEntry(TACTKey eKey, out CDNFileLocation entry) =>
        m_entries.TryGetValue(eKey, out entry);

    public bool TryGetEntry(ReadOnlySpan<byte> eKey, out CDNFileLocation entry) =>
        m_entries.TryGetValue(new TACTKey(eKey), out entry);

    private IndexManifest(Dictionary<TACTKey, CDNFileLocation> entries)
    {
        m_entries = entries;
    }

    public static async Task<IndexManifest?> LoadAsync(ProductBuildInfo targetBuild)
    {
        var sources = new List<IndexSource>();
        if (targetBuild.TACTCDNConfig == null) {
            return null;
        }

        if (targetBuild.TACTCDNConfig.Archives != null) {
            foreach (var archive in targetBuild.TACTCDNConfig.Archives.Value.Keys) {
                sources.Add(new IndexSource(archive.ToString(), CDNSourceType.Archive));
            }
        }

        if (targetBuild.TACTCDNConfig.FileIndex != null) {
            foreach (var fileIndex in targetBuild.TACTCDNConfig.FileIndex.Value.Keys) {
                sources.Add(new IndexSource(fileIndex.ToString(), CDNSourceType.Loose));
            }
        }

        if (sources.Count == 0) {
            throw new InvalidDataException("CDN config contains neither archives nor file-index.");
        }


        var results = new CDNFileLocation[sources.Count][];

        const int maxConcurrency = 16;
        using var semaphore = new SemaphoreSlim(maxConcurrency);
        var tasks = new Task[sources.Count];
        for (var i = 0; i < sources.Count; i++) {
            var index = i;
            var source = sources[i];
            tasks[i] = LoadIndex(index, source, semaphore);
        }

        await Task.WhenAll(tasks);
        var totalEntries = 0;
        foreach (var result in results) {
            totalEntries += result.Length;
        }

        var entries = new Dictionary<TACTKey, CDNFileLocation>(totalEntries);
        for (var sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++) {
            if (sources[sourceIndex].Type != CDNSourceType.Archive) {
                continue;
            }

            foreach (ref readonly var entry in results[sourceIndex].AsSpan()) {
                entries.TryAdd(entry.EKey, entry);
            }
        }

        //merge fileindexes
        for (var sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++) {
            if (sources[sourceIndex].Type != CDNSourceType.Loose) {
                continue;
            }

            foreach (ref readonly var entry in results[sourceIndex].AsSpan()) {
                entries[entry.EKey] = entry;
            }
        }

        return new IndexManifest(entries);

        async Task LoadIndex(int resultIndex, IndexSource source, SemaphoreSlim semaphores)
        {
            await semaphores.WaitAsync();
            try {
                results[resultIndex] = await LoadIndexSource(targetBuild, source);
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception e) {
                Logger.Log($"Failed to load CDN index {source.Hash}.index ({source.Type}): {e.GetType().Name}: {e.Message}");
                results[resultIndex] = [];
            } finally {
                semaphores.Release();
            }
        }
    }

    private static async Task<CDNFileLocation[]> LoadIndexSource(ProductBuildInfo targetBuild, IndexSource source)
    {
        using var buffer = await TACTAssetService.GetArchiveIndex(targetBuild, source.Hash);
        if (buffer == null) {
            throw new IOException($"Unable to download CDN index {source.Hash}.index");
        }

        var index = new CDNIndex(buffer.Value.ReadOnlySpan);
        return source.Type switch {
            CDNSourceType.Archive => ParseArchiveSource(source.Hash, index),
            CDNSourceType.Loose => ParseFileIndexSource(source.Hash, index),
            _ => throw new UnreachableException()
        };
    }

    private static CDNFileLocation[] ParseArchiveSource(string archive, CDNIndex index)
    {
        var result = new CDNFileLocation[index.Count];

        switch (index.Type) {
            case CDNIndexType.Archive: {
                for (var i = 0; i < index.Count; i++) {
                    ref readonly var entry = ref index[i];
                    result[i] = new CDNFileLocation(entry.EKey, archive, entry.Offset, entry.Size);
                }

                return result;
            }
            case CDNIndexType.Loose: {
                for (var i = 0; i < index.Count; i++) {
                    ref readonly var entry = ref index[i];
                    result[i] = new CDNFileLocation(entry.EKey, entry.Size);
                }

                return result;
            }
            case CDNIndexType.Group:
                throw new NotSupportedException($"Index {archive}.index is a group index.");
            case CDNIndexType.PatchGroup:
                throw new NotSupportedException($"Index {archive}.index is a patch-group index.");
            case CDNIndexType.Unknown:
            default:
                throw new InvalidDataException($"Unknown CDN index type for {archive}.index " +
                                               $"(offsetBytes={index.OffsetBytes}).");
        }
    }

    private static CDNFileLocation[] ParseFileIndexSource(string hash, CDNIndex index)
    {
        if (index.Type != CDNIndexType.Loose) {
            throw new InvalidDataException($"CDN file-index {hash}.index has unexpected type {index.Type} (offsetBytes={index.OffsetBytes}).");
        }

        var result = new CDNFileLocation[index.Count];
        for (var i = 0; i < index.Count; i++) {
            ref readonly var entry = ref index[i];
            result[i] = new CDNFileLocation(entry.EKey, entry.Size);
        }

        return result;
    }
}