using System.Buffers;
using System.Security.Cryptography;
using BattleNGDP.CASC;
using BattleNGDP.Core;
using BattleNGDP.TACT.CDN;
using BattleNGDP.TACT.Data;
using Microsoft.Data.Sqlite;

namespace BattleNGDP.TACT.Manifest;

/// <summary>
/// Handles the build-file-db entry present in a containerless <see cref="BuildConfig"/> 
/// </summary>
public sealed class BuildFileDBManifest
{
    public sealed record FileEntry(long Ordinal, string Path, byte[]? CKeyHex, byte[]? EHeaderKeyHex, long CSize, long ESize, long EHeaderSize);

    public FileEntry[] Files { get; }
    public IReadOnlyDictionary<string, FileEntry> ByPath { get; }

    public bool TryGetFile(string path, out FileEntry entry) =>
        ByPath.TryGetValue(path.Replace('\\', '/'), out entry!);

    private static ReadOnlySpan<byte> SqliteMagic => "SQLite format 3\0"u8;

    private BuildFileDBManifest(FileEntry[] files)
    {
        Files = files;
        var lookup = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files) {
            lookup[file.Path.Replace('\\', '/')] = file;
        }

        ByPath = lookup;
    } /*

    public static BuildFileDBManifest Parse(ReadOnlySpan<byte> data)
    {
        var rented = ArrayPool<byte>.Shared.Rent(data.Length);
        try {
            data.CopyTo(rented.AsSpan(0, data.Length));
            return Parse(rented.AsSpan(0, data.Length));
        } finally {
            ArrayPool<byte>.Shared.Return(rented, true);
        }
    }*/

    public static BuildFileDBManifest Parse(Span<byte> data)
    {
        PooledBuffer<byte>? buffer = null;
        try {
            var current = data;
            for (var layer = 0; layer < 6; layer++) {
                if (current.StartsWith(SqliteMagic)) {
                    return ParseSqlite(current);
                }

                if (current.IsBLTEEncoded()) {
                    var blte = new BLTE(current);
                    buffer?.Dispose();
                    buffer = blte.Data;
                    if (buffer.Value.Span.StartsWith(SqliteMagic)) {
                        return ParseSqlite(buffer.Value.Span);
                    }

                    current = buffer.Value.Span;
                    continue;
                }

                if (!current.IsEmpty && current[0] == (byte) 'E') {
                    ContainerCrypt.DecryptContainerlessInPlace(ref current);
                    continue;
                }

                throw new InvalidDataException(
                    $"[File DB] unexpected layer {layer}, leading bytes {Convert.ToHexString(current[..Math.Min(current.Length, 16)])}. Expected SQLite, BLTE or containerless E.");
            }

            throw new InvalidDataException("[File DB] exceeded the maximum decoding depth.");
        } finally {
            buffer?.Dispose();
        }
    }

    private static Span<byte> DecryptContainerlessInPlace(Span<byte> data)
    {
        var cursor = 1;
        if (data.Length < 1 + 1 + 8 + 1 + 4 + 1) {
            throw new InvalidDataException("[File DB] Truncated encryption header.");
        }

        int keyNameLength = data[cursor++];
        if (keyNameLength != 8 || data.Length < cursor + keyNameLength + 1) {
            throw new NotSupportedException($"[File DB] Unsupported key name length {keyNameLength}.");
        }

        ReadOnlySpan<byte> keyName = data.Slice(cursor, keyNameLength);
        cursor += keyNameLength;

        int ivLength = data[cursor++];
        if (ivLength is not (4 or 8) || data.Length < cursor + ivLength + 1) {
            throw new InvalidDataException($"[File DB] Invalid IV length {ivLength}.");
        }

        ReadOnlySpan<byte> iv = data.Slice(cursor, ivLength);
        cursor += ivLength;

        var cipher = data[cursor++];
        var ciphertext = data[cursor..];

        Span<byte> keyValue = stackalloc byte[16];
        Span<byte> nonce = stackalloc byte[8];
        nonce.Clear();
        iv.CopyTo(nonce);

        try {
            if (!TACTKeyringService.TryGetUniqueByNameBytes(keyName, keyValue)) {
                throw new KeyNotFoundException($"[File DB] Key unavailable//ambiguous {Convert.ToHexString(keyName)}.");
            }

            switch (cipher) {
                case (byte) 'S':
                    Salsa20.TransformInPlace(ciphertext, keyValue, nonce);
                    break;

                case (byte) 'A':
                    Arc4.TransformInPlace(ciphertext, keyValue, 1024, iv);
                    break;

                default:
                    throw new NotSupportedException($"[File DB] Unknown encryption mode 0x{cipher:X2}.");
            }
        } finally {
            CryptographicOperations.ZeroMemory(keyValue);
            CryptographicOperations.ZeroMemory(nonce);
        }

        return ciphertext;
    }

    /*private static void TransformArc4InPlace(Span<byte> data, ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, Arc4Variant variant)
    {
        var appendIv = variant is Arc4Variant.KeyAndIv or Arc4Variant.KeyAndIvDrop1024;
        var drop = variant is Arc4Variant.KeyOnlyDrop1024 or Arc4Variant.KeyAndIvDrop1024
            ? 1024
            : 0;

        var seedSize = key.Length + (appendIv ? nonce.Length : 0);
        Span<byte> permutation = stackalloc byte[256];
        try {
            for (var n = 0; n < 256; n++) {
                permutation[n] = (byte) n;
            }

            byte j = 0;
            for (var n = 0; n < 256; n++) {
                var at = n % seedSize;
                var k = at < key.Length ? key[at] : nonce[at - key.Length];
                j = unchecked((byte) (j + permutation[n] + k));
                (permutation[n], permutation[j]) = (permutation[j], permutation[n]);
            }

            byte i = 0;
            j = 0;
            for (var n = 0; n < drop; n++) {
                _ = NextArc4Byte(permutation, ref i, ref j);
            }

            for (var n = 0; n < data.Length; n++) {
                data[n] ^= NextArc4Byte(permutation, ref i, ref j);
            }
        } finally {
            permutation.Clear();
        }
    }*/

    private static byte NextArc4Byte(Span<byte> s, ref byte i, ref byte j)
    {
        i = unchecked((byte) (i + 1));
        j = unchecked((byte) (j + s[i]));
        (s[i], s[j]) = (s[j], s[i]);
        return s[unchecked((byte) (s[i] + s[j]))];
    }

    private static BuildFileDBManifest ParseSqlite(ReadOnlySpan<byte> sqlite)
    {
        if (!sqlite.StartsWith(SqliteMagic)) {
            throw new InvalidDataException("[File DB] is not an SQLite database.");
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"BattleNGDP-filedb-{Guid.NewGuid():N}.sqlite");

        try {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                stream.Write(sqlite);
            }

            var builder = new SqliteConnectionStringBuilder {
                DataSource = tempPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            };

            var files = new List<FileEntry>();

            using (var db = new SqliteConnection(builder.ToString())) {
                db.Open();

                using var command = db.CreateCommand();
                command.CommandText = """
                                      SELECT "ordinal", "cKey", "eHeaderKey", "cSize",
                                             "eSize", "eHeaderSize", "Path"
                                      FROM "files"
                                      ORDER BY "ordinal"
                                      """;

                using var reader = command.ExecuteReader();
                while (reader.Read()) {
                    var ordinal = reader.GetInt64(0);
                    var cKey = ReadKeyColumn(reader, 1);
                    var eHeaderKey = ReadKeyColumn(reader, 2);
                    var cSize = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
                    var eSize = reader.IsDBNull(4) ? 0 : reader.GetInt64(4);
                    var eHeaderSize = reader.IsDBNull(5) ? 0 : reader.GetInt64(5);
                    var path = reader.IsDBNull(6) ? "" : reader.GetString(6);

                    if (string.IsNullOrWhiteSpace(path) || cSize < 0 || eSize < 0 || eHeaderSize < 0) {
                        throw new InvalidDataException($"File DB: invalid row at ordinal {ordinal}.");
                    }

                    files.Add(new FileEntry(ordinal, path, cKey, eHeaderKey, cSize, eSize, eHeaderSize));
                }
            }

            return new BuildFileDBManifest([.. files]);
        } finally {
            try {
                File.Delete(tempPath);
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static byte[]? ReadKeyColumn(SqliteDataReader reader, int index)
    {
        if (reader.IsDBNull(index)) {
            return null;
        }

        var value = reader.GetValue(index);
        if (value is not byte[] binary) {
            throw new InvalidDataException($"[File DB] invalid key column {index}. Expected byte array but got {value.GetType().Name}");
        }

        return binary;
    }

    public static async Task<BuildFileDBManifest> LoadAsync(ProductBuildInfo buildInfo, EncodingManifest encodingManifest,
        IndexManifest indexManifest)
    {
        if (!encodingManifest.TryGetCKeyEntry(buildInfo.TACTBuildConfig.BuildFileDB.Value.CKey.AsSpan(), out var ckeyEntry)) {
            throw new InvalidDataException("[File DB] CKey lookup failed...");
        }

        CDNFileLocation? cdnLocation = null;
        var candidates = encodingManifest.GetEKeys(ckeyEntry);
        foreach (var candidate in candidates) {
            if (!indexManifest.TryGetEntry(candidate, out var loc)) {
                continue;
            }

            cdnLocation = loc;
        }

        if (cdnLocation == null) {
            throw new InvalidDataException("[File DB] CDN location lookup failed...");
        }

        var hash = cdnLocation.Value.Archive ?? cdnLocation.Value.EKey.ToString();
        var bounds = new TACTArchiveBounds {
            Offset = cdnLocation.Value.Offset,
            Size = cdnLocation.Value.Size
        };

        Console.WriteLine($"[+]     Found containerless mode for {buildInfo.TACTName} v{buildInfo.Version} at {hash}");
        using var curBuffer = await TACTAssetService.GetArchive(buildInfo, hash, bounds);
        if (curBuffer == null) {
            throw new InvalidDataException("CDN did not provide any data for file DB key.");
        }

        return Parse(curBuffer.Value.Span);
    }
}