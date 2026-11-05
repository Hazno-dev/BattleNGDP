using System.Security.Cryptography;
using System.Text;
using BattleNGDP.CASC;
using BattleNGDP.Core;
using BattleNGDP.TACT.CDN;
using BattleNGDP.TACT.Data;

namespace BattleNGDP.TACT.Manifest;

public readonly struct EncodingPageIndex(TACTKey firstKey, TACTKey pageHash)
{
    public readonly TACTKey FirstKey = firstKey;
    public readonly TACTKey PageHash = pageHash;
}

public readonly struct EncodingContentEntry(TACTKey ckey, ulong fileSize, int ekeyOffset, byte ekeyCount)
{
    public readonly TACTKey CKey = ckey;
    public readonly ulong FileSize = fileSize;
    public readonly int EKeyOffset = ekeyOffset;
    public readonly byte EKeyCount = ekeyCount;
}

public readonly struct EncodingEncodedEntry(TACTKey ekey, uint especIndex, ulong fileSize, string espec)
{
    public readonly TACTKey EKey = ekey;
    public readonly uint ESpecIndex = especIndex;
    public readonly ulong FileSize = fileSize;
    public readonly string ESpec = espec;
}

/// <summary>
/// Maps CKey to EKeys and handles especs
/// </summary>
public sealed class EncodingManifest
{
    public byte Version { get; }
    public byte CKeyHashSize { get; }
    public byte EKeyHashSize { get; }
    public int CKeyPageSize { get; }
    public int EKeyPageSize { get; }
    public uint CKeyPageCount { get; }
    public uint EKeyPageCount { get; }
    public byte Flags { get; }
    public uint ESpecBlockSize { get; }
    public EncodingPageIndex[] CKeyPageIndex { get; }
    public EncodingPageIndex[] EKeyPageIndex { get; }

    public string? FileESpec { get; }

    private readonly EncodingContentEntry[] m_cKeyEntries;
    private readonly EncodingEncodedEntry[] m_eKeyEntries;

    private readonly TACTKey[] m_contentEKeys;

    private readonly Dictionary<TACTKey, int> m_cKeyLookup;
    private readonly Dictionary<TACTKey, int> m_eKeyLookup;

    private readonly string[] m_eSpecs;

    public ReadOnlySpan<EncodingContentEntry> CKeyEntries =>
        m_cKeyEntries;

    public ReadOnlySpan<EncodingEncodedEntry> EKeyEntries =>
        m_eKeyEntries;

    public IReadOnlyList<string> ESpecs =>
        m_eSpecs;

    public ReadOnlySpan<TACTKey> GetEKeys(in EncodingContentEntry entry) =>
        m_contentEKeys.AsSpan(entry.EKeyOffset, entry.EKeyCount);

    public EncodingManifest(ReadOnlySpan<byte> data, bool verifyPageHashes = false)
    {
        var reader = new SpanReader(data);
        if (!reader.ReadBytes(2).SequenceEqual("EN"u8)) {
            throw new InvalidDataException("Invalid encoding manifest signature.");
        }

        Version = reader.ReadByte();

        if (Version != 1) {
            throw new NotSupportedException($"Unsupported encoding manifest version {Version}.");
        }

        CKeyHashSize = reader.ReadByte();
        EKeyHashSize = reader.ReadByte();

        if (CKeyHashSize is 0 or > 16) {
            throw new InvalidDataException($"Invalid CKey hash size {CKeyHashSize}.");
        }

        if (EKeyHashSize is 0 or > 16) {
            throw new InvalidDataException($"Invalid EKey hash size {EKeyHashSize}.");
        }

        CKeyPageSize = checked(reader.ReadUInt16BE() * 1024);
        EKeyPageSize = checked(reader.ReadUInt16BE() * 1024);
        if (CKeyPageSize == 0 || EKeyPageSize == 0) {
            throw new InvalidDataException("Encoding page size cannot be zero.");
        }

        CKeyPageCount = reader.ReadUInt32BE();
        EKeyPageCount = reader.ReadUInt32BE();
        Flags = reader.ReadByte();

        if (Flags != 0) {
            throw new NotSupportedException($"Unsupported encoding flags 0x{Flags:X2}.");
        }

        ESpecBlockSize = reader.ReadUInt32BE();
        var especBlock = reader.ReadBytes(checked((int) ESpecBlockSize));
        m_eSpecs = ParseESpecBlock(especBlock);

        //ckey to page index
        CKeyPageIndex = ReadPageIndex(ref reader, CKeyPageCount, CKeyHashSize);

        //ckey to ekey pages 
        var cKeyEntries = new List<EncodingContentEntry>();
        var contentEKeys = new List<TACTKey>();
        m_cKeyLookup = new Dictionary<TACTKey, int>();

        for (var pageIndex = 0; pageIndex < checked((int) CKeyPageCount); pageIndex++) {
            var page = reader.ReadBytes(CKeyPageSize);
            if (verifyPageHashes) {
                VerifyPageHash(page, CKeyPageIndex[pageIndex].PageHash, "CKey", pageIndex);
            }

            ParseCKeyPage(page, pageIndex, cKeyEntries, contentEKeys);
        }

        m_cKeyEntries = [.. cKeyEntries];
        m_contentEKeys = [.. contentEKeys];

        //ekey to page index
        EKeyPageIndex = ReadPageIndex(ref reader, EKeyPageCount, EKeyHashSize);

        //ekey to espec pages
        var eKeyEntries = new List<EncodingEncodedEntry>();
        m_eKeyLookup = new Dictionary<TACTKey, int>();

        for (var pageIndex = 0; pageIndex < checked((int) EKeyPageCount); pageIndex++) {
            var page = reader.ReadBytes(EKeyPageSize);
            if (verifyPageHashes) {
                VerifyPageHash(page, EKeyPageIndex[pageIndex].PageHash, "EKey", pageIndex);
            }

            ParseEKeyPage(page, pageIndex, eKeyEntries);
        }

        m_eKeyEntries = [.. eKeyEntries];


        //remaining data == file espec
        var fileESpec = reader.ReadRemaining();
        FileESpec = DecodeFileESpec(fileESpec);
    }

    public bool TryGetCKeyEntry(ReadOnlySpan<byte> ckey, out EncodingContentEntry entry)
    {
        if (ckey.Length != CKeyHashSize) {
            entry = default;
            return false;
        }

        if (!m_cKeyLookup.TryGetValue(new TACTKey(ckey), out var index)) {
            entry = default;
            return false;
        }

        entry = m_cKeyEntries[index];
        return true;
    }

    public bool TryGetCKeyEntry(TACTKey ckey, out EncodingContentEntry entry)
    {
        if (!m_cKeyLookup.TryGetValue(ckey, out var index)) {
            entry = default;
            return false;
        }

        entry = m_cKeyEntries[index];
        return true;
    }

    public bool TryGetEKeyEntry(ReadOnlySpan<byte> ekey, out EncodingEncodedEntry entry)
    {
        if (ekey.Length != EKeyHashSize) {
            entry = default;
            return false;
        }

        if (!m_eKeyLookup.TryGetValue(new TACTKey(ekey), out var index)) {
            entry = default;
            return false;
        }

        entry = m_eKeyEntries[index];
        return true;
    }

    public bool TryGetEKeyEntry(TACTKey ekey, out EncodingEncodedEntry entry)
    {
        if (!m_eKeyLookup.TryGetValue(ekey, out var index)) {
            entry = default;
            return false;
        }

        entry = m_eKeyEntries[index];
        return true;
    }

    private static EncodingPageIndex[] ReadPageIndex(ref SpanReader reader, uint pageCount, int keySize)
    {
        var count = checked((int) pageCount);
        var result = new EncodingPageIndex[count];

        for (var i = 0; i < count; i++) {
            var firstKey = new TACTKey(reader.ReadBytes(keySize));
            var pageHash = new TACTKey(reader.ReadBytes(16));
            result[i] = new EncodingPageIndex(firstKey, pageHash);
        }

        return result;
    }

    private void ParseCKeyPage(ReadOnlySpan<byte> page, int pageIndex, List<EncodingContentEntry> entries, List<TACTKey> ekeys)
    {
        var reader = new SpanReader(page);
        TACTKey? firstKey = null;

        while (reader.Remaining > 0) {
            var ekeyCount = reader.ReadByte();
            if (ekeyCount == 0) {
                break;
            }

            var fileSize = reader.ReadUInt40BE();
            var ckey = new TACTKey(reader.ReadBytes(CKeyHashSize));
            firstKey ??= ckey;

            var ekeyOffset = ekeys.Count;
            for (var i = 0; i < ekeyCount; i++) {
                ekeys.Add(new TACTKey(reader.ReadBytes(EKeyHashSize)));
            }

            var entryIndex = entries.Count;

            var entry = new EncodingContentEntry(ckey, fileSize, ekeyOffset, ekeyCount);

            if (!m_cKeyLookup.TryAdd(ckey, entryIndex)) {
                throw new InvalidDataException($"Duplicate CKey {ckey}.");
            }

            entries.Add(entry);
        }

        if (firstKey is { } key && key != CKeyPageIndex[pageIndex].FirstKey) {
            throw new InvalidDataException(
                $"CKey page {pageIndex} first key mismatch... Expected {CKeyPageIndex[pageIndex].FirstKey} but got {key}.");
        }
    }

    private void ParseEKeyPage(ReadOnlySpan<byte> page, int pageIndex, List<EncodingEncodedEntry> entries)
    {
        var reader = new SpanReader(page);
        TACTKey? firstKey = null;

        var prefixSize = EKeyHashSize + sizeof(uint);
        while (reader.Remaining >= prefixSize) {
            var ekeyBytes = reader.ReadBytes(EKeyHashSize);
            var especIndex = reader.ReadUInt32BE();
            if (especIndex == uint.MaxValue) {
                break;
            }

            if (especIndex == 0 && IsZero(ekeyBytes) && IsZero(reader.RemainingSpan)) {
                break;
            }

            if (reader.Remaining < 5) {
                throw new InvalidDataException($"Truncated EKey entry in page {pageIndex}.");
            }

            var fileSize = reader.ReadUInt40BE();
            if (especIndex >= m_eSpecs.Length) {
                throw new InvalidDataException($"EKey page {pageIndex} references invalid espec index {especIndex}.");
            }

            var ekey = new TACTKey(ekeyBytes);
            firstKey ??= ekey;

            var entryIndex = entries.Count;
            if (!m_eKeyLookup.TryAdd(ekey, entryIndex)) {
                throw new InvalidDataException($"Duplicate EKey {ekey}.");
            }

            entries.Add(new EncodingEncodedEntry(ekey, especIndex, fileSize, m_eSpecs[especIndex]));
        }

        if (firstKey is { } key && key != EKeyPageIndex[pageIndex].FirstKey) {
            throw new InvalidDataException(
                $"EKey page {pageIndex} first key mismatch... Expected {EKeyPageIndex[pageIndex].FirstKey} but got {key}.");
        }
    }

    private static string[] ParseESpecBlock(ReadOnlySpan<byte> data)
    {
        var result = new List<string>();

        while (!data.IsEmpty) {
            var terminator = data.IndexOf((byte) 0);

            if (terminator < 0) {
                throw new InvalidDataException("Unterminated ESpec string.");
            }

            if (terminator == 0) {
                throw new InvalidDataException("Empty ESpec string.");
            }

            result.Add(Encoding.ASCII.GetString(data[..terminator]));

            data = data[(terminator + 1)..];
        }

        return [.. result];
    }

    private static string? DecodeFileESpec(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) {
            return null;
        }

        var terminator = data.IndexOf((byte) 0);
        if (terminator >= 0) {
            data = data[..terminator];
        }

        if (data.IsEmpty) {
            return null;
        }

        return Encoding.ASCII.GetString(data);
    }

    private static void VerifyPageHash(ReadOnlySpan<byte> page, TACTKey expected, string table, int pageIndex)
    {
        Span<byte> digest = stackalloc byte[16];
        MD5.HashData(page, digest);

        var actual = new TACTKey(digest);
        if (actual != expected) {
            throw new InvalidDataException($"{table} page {pageIndex} MD5 mismatch... Expected {expected} but got {actual}.");
        }
    }

    private static bool IsZero(ReadOnlySpan<byte> data)
    {
        foreach (var value in data) {
            if (value != 0) {
                return false;
            }
        }

        return true;
    }

    public static async Task<EncodingManifest?> LoadAsync(ProductBuildInfo buildInfo)
    {
        var encoding = buildInfo.TACTBuildConfig?.Encoding;
        if (encoding == null) {
            throw new ArgumentNullException(nameof(buildInfo), $"Failed to encoding for {buildInfo.TACTName} v{buildInfo.Version}.\n" +
                                                               $"BattleNGDP does not support plaintext mode.");
        }

        if (encoding.Value.EKey == null) {
            throw new ArgumentNullException(nameof(buildInfo), "BattleNGDP doesn't currently support CKey lookup for build config data.");
        }

        var encodingHash = encoding.Value.EKey.Value.ToString();
        using var buffer = await TACTAssetService.GetDataBLTE(buildInfo, encodingHash);
        if (buffer == null) {
            throw new NullReferenceException($"Failed to get encoding {encodingHash} from CDNs for {buildInfo.TACTName} v{buildInfo.Version}");
        }

        return new EncodingManifest(buffer.Value.Span);
    }
}