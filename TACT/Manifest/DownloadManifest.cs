using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BattleNGDP.CASC;
using BattleNGDP.Core;
using BattleNGDP.TACT.CDN;
using BattleNGDP.TACT.Data;

namespace BattleNGDP.TACT.Manifest;

/// <summary>
/// Handles files stored in the data archives
/// </summary>
public sealed class DownloadManifest
{
    public readonly record struct Entry(TACTKey EKey, ulong EncodedSize, byte RawPriority, int Priority, uint? Checksum, uint Flags);

    public sealed class Tag(string name, ushort type, byte[] bitmap)
    {
        public string Name { get; } = name;
        public ushort Type { get; } = type;
        public ReadOnlyMemory<byte> Bitmap => bitmap;

        public override string ToString() =>
            $"{Name} (type={Type})";

        public bool Includes(int entryIndex)
        {
            //Bit 7 of byte 0 corresponds to entry index 0
            if (entryIndex < 0 || (uint) (entryIndex >> 3) >= (uint) bitmap.Length) {
                return false;
            }

            return (bitmap[entryIndex >> 3] & (0x80 >> (entryIndex & 7))) != 0;
        }
    }


    public byte Version { get; }
    public byte EKeySize { get; }
    public bool HasChecksums { get; }
    public byte FlagByteCount { get; }
    public byte BasePriority { get; }

    public int EntryCount => m_entries.Length;
    public int TagCount => m_tags.Length;
    public IReadOnlyList<Entry> Entries => m_entries;
    public IReadOnlyList<Tag> Tags => m_tags;

    private readonly Entry[] m_entries;
    private readonly Tag[] m_tags;
    private readonly Dictionary<TACTKey, int> m_keyToIndex;

    private DownloadManifest(byte version, byte eKeySize, bool hasChecksums, byte flagByteCount, byte basePriority, Entry[] entries, Tag[] tags)
    {
        Version = version;
        EKeySize = eKeySize;
        HasChecksums = hasChecksums;
        FlagByteCount = flagByteCount;
        BasePriority = basePriority;
        m_entries = entries;
        m_tags = tags;

        m_keyToIndex = new Dictionary<TACTKey, int>(entries.Length);
        for (var i = 0; i < entries.Length; i++) {
            m_keyToIndex.TryAdd(entries[i].EKey, i);
        }
    }

    public bool TryGetEntry(TACTKey eKey, out Entry entry)
    {
        if (m_keyToIndex.TryGetValue(eKey, out var index)) {
            entry = m_entries[index];
            return true;
        }

        entry = default;
        return false;
    }

    public IEnumerable<Entry> GetEntriesWithTag(string name, ushort? type = null)
    {
        for (var i = 0; i < m_entries.Length; i++) {
            foreach (var tag in m_tags) {
                if (string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase) && (!type.HasValue || tag.Type == type.Value) &&
                    tag.Includes(i)) {
                    yield return m_entries[i];
                    break;
                }
            }
        }
    }

    public static DownloadManifest Parse(ReadOnlySpan<byte> decoded)
    {
        if (decoded.Length < 11 || decoded[0] != (byte) 'D' || decoded[1] != (byte) 'L') {
            throw new InvalidDataException("Not a TACT DL download manifest.");
        }

        var position = 2;
        var version = Take(decoded, ref position, 1)[0];
        if (version is < 1 or > 3) {
            throw new InvalidDataException($"Unsupported DL version {version}.");
        }

        var eKeySize = Take(decoded, ref position, 1)[0];
        if (eKeySize is < 1 or > 16) {
            throw new InvalidDataException($"Unsupported DL EKey size {eKeySize}.");
        }

        var hasChecksums = Take(decoded, ref position, 1)[0] != 0;
        var entryCount = BinaryPrimitives.ReadUInt32BigEndian(Take(decoded, ref position, 4));
        var tagCount = BinaryPrimitives.ReadUInt16BigEndian(Take(decoded, ref position, 2));

        byte flagByteCount = 0;
        byte basePriority = 0;

        if (version >= 2) {
            flagByteCount = Take(decoded, ref position, 1)[0];
            if (flagByteCount > 4) {
                throw new InvalidDataException($"Unsupported DL flags width {flagByteCount}.");
            }
        }

        if (version >= 3) {
            basePriority = Take(decoded, ref position, 1)[0];
            Take(decoded, ref position, 3);
        }

        var entryWidth = eKeySize + 5 + 1 + (hasChecksums ? 4 : 0) + flagByteCount;
        var entriesEnd = (long) position + (long) entryCount * entryWidth;
        if (entryCount > int.MaxValue || entriesEnd > decoded.Length) {
            throw new InvalidDataException("DL entries exceed the available manifest data.");
        }

        var entries = new Entry[checked((int) entryCount)];
        for (var i = 0; i < entries.Length; i++) {
            var eKey = new TACTKey(Take(decoded, ref position, eKeySize));

            var sizeBytes = Take(decoded, ref position, 5);
            var encodedSize = ((ulong) sizeBytes[0] << 32) |
                              ((ulong) sizeBytes[1] << 24) |
                              ((ulong) sizeBytes[2] << 16) |
                              ((ulong) sizeBytes[3] << 8) |
                              sizeBytes[4];

            var rawPriority = Take(decoded, ref position, 1)[0];

            uint? checksum = null;
            if (hasChecksums) {
                checksum = BinaryPrimitives.ReadUInt32BigEndian(Take(decoded, ref position, 4));
            }

            uint flags = 0;
            foreach (var b in Take(decoded, ref position, flagByteCount)) {
                flags = (flags << 8) | b;
            }

            entries[i] = new Entry(eKey, encodedSize, rawPriority,
                rawPriority - basePriority,
                checksum, flags);
        }

        var bitmapSize = checked((int) ((entryCount + 7UL) / 8UL));
        var tags = new Tag[tagCount];
        for (var i = 0; i < tags.Length; i++) {
            var zeroIndex = decoded[position..].IndexOf((byte) 0);
            if (zeroIndex < 0) {
                throw new InvalidDataException($"DL tag {i} has no NUL terminator.");
            }

            var name = Encoding.UTF8.GetString(Take(decoded, ref position, zeroIndex));
            position++;
            var type = BinaryPrimitives.ReadUInt16BigEndian(Take(decoded, ref position, 2));
            var bitmap = Take(decoded, ref position, bitmapSize).ToArray();
            tags[i] = new Tag(name, type, bitmap);
        }

        return new DownloadManifest(version, eKeySize, hasChecksums, flagByteCount, basePriority, entries, tags);
    }

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> data, ref int offset, int count)
    {
        if (count < 0 || offset < 0 || offset > data.Length - count) {
            throw new InvalidDataException("Truncated DL download manifest.");
        }

        var value = data.Slice(offset, count);
        offset += count;
        return value;
    }

    public static async Task<DownloadManifest?> LoadAsync(ProductBuildInfo buildInfo)
    {
        var download = buildInfo.TACTBuildConfig?.Download;
        if (download?.EKey == null) {
            return null;
        }

        var downloadHash = download.Value.EKey.Value.ToString();
        using var buffer = await TACTAssetService.GetDataBLTE(buildInfo, downloadHash);
        if (buffer == null) {
            throw new NullReferenceException($"Failed to get download {downloadHash} from CDNs for {buildInfo.TACTName} v{buildInfo.Version}");
        }

        return Parse(buffer.Value.Span);
    }
}