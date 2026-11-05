using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using BattleNGDP.Core;
using BattleNGDP.TACT.CDN;

namespace BattleNGDP.TACT.Data;

/// <summary>
/// A TACT encryption key value (16 bytes), stored inline without a managed byte array.
/// Unlike an Armadillo .ak key, this is a BLTE 'E' chunk key.
/// </summary>
public readonly record struct TACTKeyValue(ulong First, ulong Second)
{
    public const int Length = 16;

    public static TACTKeyValue FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Length) {
            throw new ArgumentException("TACT key values must be 16 bytes.", nameof(bytes));
        }

        return new TACTKeyValue(BinaryPrimitives.ReadUInt64BigEndian(bytes[..8]), BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]));
    }

    /// <summary>Copies the key into caller-owned memory (stackalloc is fine).</summary>
    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < Length) {
            throw new ArgumentException("Destination must hold at least 16 bytes.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt64BigEndian(destination[..8], First);
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(8, 8), Second);
    }

    public override string ToString() => $"{First:x16}{Second:x16}";
}

public enum TACTKeyAddResult
{
    Added,
    Duplicate,
    AddedAlternateValue
}

public readonly record struct TACTKeyImportResult(int Added, int Duplicates, int AlternateValues, int InvalidLines);

/// <summary>
/// Stores and manages keyring local keyring data for asset decryption
/// </summary>
public static class TACTKeyringService
{
    private static readonly Lock s_sync = new();
    private static Dictionary<ulong, List<TACTKeyValue>>? s_keys;

    public static int KeyIdCount
    {
        get
        {
            Deserialize();

            lock (s_sync) {
                return s_keys.Count;
            }
        }
    }

    public static int KeyPairCount
    {
        get
        {
            Deserialize();

            lock (s_sync) {
                var count = 0;
                foreach (var values in s_keys.Values) {
                    count += values.Count;
                }

                return count;
            }
        }
    }

    public static async Task<TACTKeyImportResult> ImportRemote(string keyring, string? remotePath, string tactName, string? armadillo)
    {
        Deserialize();

        var prevHeadOnly = TACTAssetService.HeadOnly;
        TACTAssetService.HeadOnly = true;
        using var remoteKeyring = await TACTAssetService.Get($"{remotePath}/config", $"{tactName}/Keyring", keyring, armadillo, null);
        if (remoteKeyring == null) {
            Logger.Log($"Failed to load remote keyring {keyring}");
            TACTAssetService.HeadOnly = prevHeadOnly;
            return new TACTKeyImportResult();
        }

        TACTAssetService.HeadOnly = prevHeadOnly;
        return Import(remoteKeyring.Value.Span);
    }

    public static TACTKeyImportResult Import(ReadOnlySpan<byte> utf8, bool strict = false)
    {
        Deserialize();

        int added = 0, duplicates = 0, alternates = 0, invalid = 0;
        var lineNumber = 0;

        while (!utf8.IsEmpty) {
            var newline = utf8.IndexOf((byte) '\n');
            ReadOnlySpan<byte> line;

            if (newline < 0) {
                line = utf8;
                utf8 = default;
            } else {
                line = utf8[..newline];
                utf8 = utf8[(newline + 1)..];
            }

            lineNumber++;

            var comment = line.IndexOf((byte) '#');
            if (comment >= 0) {
                line = line[..comment];
            }

            line = StringUtility.TrimAscii(line);
            if (line.IsEmpty) {
                continue;
            }

            if (!TryParseLine(line, out var id, out var value)) {
                invalid++;
                if (strict) {
                    throw new InvalidDataException($"Invalid keyring entry on line {lineNumber}.");
                }

                continue;
            }

            switch (AddUnsafe(id, value)) {
                case TACTKeyAddResult.Added: {
                    added++;
                    break;
                }
                case TACTKeyAddResult.Duplicate: {
                    duplicates++;
                    break;
                }
                case TACTKeyAddResult.AddedAlternateValue: {
                    added++;
                    alternates++;
                    break;
                }
                default: {
                    throw new ArgumentOutOfRangeException();
                }
            }
        }

        if (added > 0) {
            Serialize();
        }

        return new TACTKeyImportResult(added, duplicates, alternates, invalid);
    }

    public static TACTKeyAddResult Add(ulong id, ReadOnlySpan<byte> keyValue)
    {
        Deserialize();

        return AddUnsafe(id, TACTKeyValue.FromBytes(keyValue));
    }

    private static TACTKeyAddResult AddUnsafe(ulong id, TACTKeyValue keyValue)
    {
        lock (s_sync) {
            if (!s_keys!.TryGetValue(id, out var candidates)) {
                s_keys.Add(id, new List<TACTKeyValue>(1) { keyValue });
                return TACTKeyAddResult.Added;
            }

            if (candidates.Contains(keyValue)) {
                return TACTKeyAddResult.Duplicate;
            }

            candidates.Add(keyValue);
            return TACTKeyAddResult.AddedAlternateValue;
        }
    }

    public static bool Contains(ulong id, ReadOnlySpan<byte> keyValue)
    {
        Deserialize();

        return ContainsUnsafe(id, TACTKeyValue.FromBytes(keyValue));
    }

    private static bool ContainsUnsafe(ulong id, TACTKeyValue keyValue)
    {
        lock (s_sync) {
            return s_keys!.TryGetValue(id, out var candidates) && candidates.Contains(keyValue);
        }
    }

    public static bool TryGetUnique(ulong id, Span<byte> destination)
    {
        if (destination.Length < TACTKeyValue.Length) {
            throw new ArgumentException("Destination must hold 16 bytes.", nameof(destination));
        }

        Deserialize();

        lock (s_sync) {
            if (!s_keys.TryGetValue(id, out var candidates) || candidates.Count != 1) {
                return false;
            }

            candidates[0].CopyTo(destination);
            return true;
        }
    }

    public static bool TryGetUniqueByNameBytes(ReadOnlySpan<byte> keyName, Span<byte> destination, bool reversed = false)
    {
        if (keyName.Length != 8) {
            throw new ArgumentException("BLTE key-name field must be 8 bytes.", nameof(keyName));
        }

        var id = reversed
            ? BinaryPrimitives.ReadUInt64LittleEndian(keyName)
            : BinaryPrimitives.ReadUInt64BigEndian(keyName);

        return TryGetUnique(id, destination);
    }

    public static TACTKeyValue[] GetCandidates(ulong id)
    {
        Deserialize();

        lock (s_sync) {
            return s_keys.TryGetValue(id, out var candidates)
                ? candidates.ToArray()
                : Array.Empty<TACTKeyValue>();
        }
    }

    private static bool TryParseLine(ReadOnlySpan<byte> line, out ulong id, out TACTKeyValue value)
    {
        id = 0;
        value = default;

        var separator = line.IndexOf((byte) '=');
        if (separator < 0) {
            for (var i = 0; i < line.Length; i++) {
                if (!StringUtility.IsAsciiWhitespace(line[i])) {
                    continue;
                }

                separator = i;
                break;
            }
        }

        if (separator <= 0) {
            return false;
        }

        var name = StringUtility.TrimAscii(line[..separator]);
        var hexValue = StringUtility.TrimAscii(line[(separator + 1)..]);

        if (!hexValue.IsEmpty && hexValue[0] == '=') {
            hexValue = StringUtility.TrimAscii(hexValue[1..]);
        }

        if (name.Length >= 4 && StringUtility.ToLowerAscii(name[0]) == (byte) 'k'
                             && StringUtility.ToLowerAscii(name[1]) == (byte) 'e'
                             && StringUtility.ToLowerAscii(name[2]) == (byte) 'y'
                             && name[3] == (byte) '-') {
            name = name[4..];
        }

        if (name.Length != 16 || hexValue.Length != 32) {
            return false;
        }

        if (!TryReadHex64(name, out id) || !TryReadHex64(hexValue[..16], out var first) || !TryReadHex64(hexValue[16..], out var second)) {
            return false;
        }

        value = new TACTKeyValue(first, second);
        return true;
    }

    private static bool TryReadHex64(ReadOnlySpan<byte> hex, out ulong result)
    {
        result = 0;
        if (hex.Length != 16) {
            return false;
        }

        foreach (var character in hex) {
            var nibble = StringUtility.DecodeHexNibble(character);
            if (nibble < 0) {
                return false;
            }

            result = (result << 4) | (uint) nibble;
        }

        return true;
    }

    [MemberNotNull(nameof(s_keys))]
    private static void Deserialize()
    {
        lock (s_sync) {
            if (s_keys is not null) {
                return;
            }

            s_keys = new Dictionary<ulong, List<TACTKeyValue>>();
            var path = Path.Combine(Config.Root, "Keyring");
            if (!File.Exists(path)) {
                return;
            }

            var data = File.ReadAllBytes(path);
            Import(data, true);
        }
    }

    private static void Serialize()
    {
        Deserialize();

        lock (s_sync) {
            var path = Path.Combine(Config.Root, "Keyring");
            var pathTemp = Path.Combine(Config.Root, $"Keyring.{Guid.NewGuid():N}.tmp");
            try {
                using (var writer = new StreamWriter(pathTemp, false, new UTF8Encoding(false))) {
                    writer.WriteLine("# TACT BLTE encryption keyring. ID bytes are stored in printed hex order.");

                    var ids = new List<ulong>(s_keys.Keys);
                    ids.Sort();
                    foreach (var id in ids) {
                        var values = new List<TACTKeyValue>(s_keys[id]);
                        values.Sort(static (a, b) => {
                            var result = a.First.CompareTo(b.First);
                            return result != 0 ? result : a.Second.CompareTo(b.Second);
                        });

                        foreach (var value in values) {
                            writer.WriteLine($"key-{id:x16} = {value}");
                        }
                    }
                }

                File.Move(pathTemp, path, true);
            } finally {
                if (File.Exists(pathTemp)) {
                    File.Delete(pathTemp);
                }
            }
        }
    }
}