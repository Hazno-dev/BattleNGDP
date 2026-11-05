using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using BattleNGDP.CASC;
using BattleNGDP.Core;
using BattleNGDP.TACT.CDN;
using BattleNGDP.TACT.Data;
using BattleNGDP.TACT.Manifest;
using Sharprompt;

namespace BattleNGDP.Runner;

public class RunnerInstallFiles : IRunner
{
    public enum MappedFileType
    {
        Install,
        Containerless
    }

    private struct MappedInstallFile
    {
        public ReadOnlyMemory<byte> CKey { get; set; }
        public ReadOnlyMemory<byte>? EKey { get; set; }
        public long Size { get; set; }

        public CDNFileLocation[]? Locations { get; set; }
        public string SizeStr { get; set; }
        public string UserInstallPath { get; set; }
        public string RelativePath { get; set; }

        public MappedFileType Type { get; set; }

        public static string Format(MappedInstallFile file)
        {
            if (file.Locations == null || file.Locations.Length == 0) {
                return $"{file.SizeStr,-14} {file.RelativePath,-64} [NOT FOUND]";
            }

            return $"{file.SizeStr,-14} {file.RelativePath,-64}";
        }
    }

    public string Name => "Install Files";
    public string Description => "Read and grab install files.";
    public int Priority => 0;

    private List<MappedInstallFile> m_installFiles = [];

    public bool CanExecute(RunnerCtx ctx) =>
        (ctx.BuildInfo != null && ctx.BuildInfo.InstallManifest is { Files.Length: > 0 }) || ctx.BuildFileDB is { Files.Length: > 0 };

    public async Task PreExecute(RunnerCtx ctx)
    {
        m_installFiles.Clear();
        foreach (var dbFile in ctx.BuildFileDB?.Files ?? []) {
            var map = new MappedInstallFile {
                CKey = dbFile.CKeyHex,
                EKey = dbFile.EHeaderKeyHex,
                Size = dbFile.CSize,
                SizeStr = StringUtility.BytesToString(dbFile.CSize),
                UserInstallPath = Path.GetFullPath(Path.Join(Config.Root, $"{ctx.BuildInfo.TACTName}/Install/{ctx.BuildInfo.Version}/{dbFile.Path}")),
                RelativePath = dbFile.Path,
                Type = MappedFileType.Containerless
            };

            m_installFiles.Add(map);
        }

        foreach (var file in ctx.BuildInfo!.InstallManifest?.Files ?? []) {
            var map = new MappedInstallFile {
                CKey = file.CKey.AsMemory(),
                EKey = null,
                Size = file.Size,
                SizeStr = StringUtility.BytesToString(file.Size),
                UserInstallPath = Path.GetFullPath(Path.Join(Config.Root, $"{ctx.BuildInfo.TACTName}/Install/{ctx.BuildInfo.Version}/{file.Name}")),
                RelativePath = file.Name,
                Type = MappedFileType.Install
            };

            m_installFiles.Add(map);
        }

        var cdnLocationBuf = new List<CDNFileLocation>();
        if (ctx.EncodingManifest != null) {
            for (var i = 0; i < m_installFiles.Count; i++) {
                var map = m_installFiles[i];
                if (!ctx.EncodingManifest.TryGetCKeyEntry(map.CKey.Span, out var ckeyEntry)) {
                    if (map.EKey == null) {
                        continue;
                    }

                    if (!ctx.EncodingManifest.TryGetCKeyEntry(map.EKey.Value.Span, out ckeyEntry)) {
                        continue;
                    }
                }

                cdnLocationBuf.Clear();
                if (ctx.IndexManifest != null) {
                    foreach (var eKey in ctx.EncodingManifest.GetEKeys(ckeyEntry)) {
                        if (!ctx.EncodingManifest.TryGetEKeyEntry(eKey, out var eKeyEntry)) {
                            //Logger.Log($"  EKey: {eKeyEntry.EKey.ToString().ToLowerInvariant()} -> {eKeyEntry.FileSize} bytes ({eKeyEntry.ESpecIndex})");
                            if (!ctx.IndexManifest.TryGetEntry(eKey, out var eKeylocation)) {
                                //Logger.Log($"  EKey: {eKey} -> NOT FOUND IN CDN INDEX");
                                continue;
                            }

                            cdnLocationBuf.Add(eKeylocation);
                            continue;
                        }

                        if (!ctx.IndexManifest.TryGetEntry(eKeyEntry.EKey, out var location)) {
                            //Logger.Log($"  EKey: {eKey} -> NOT FOUND IN CDN INDEX");
                            continue;
                        }

                        cdnLocationBuf.Add(location);
                    }
                }

                if (cdnLocationBuf.Count > 0) {
                    map.Locations = [.. cdnLocationBuf];
                }

                m_installFiles[i] = map;
            }
        }

        m_installFiles.Sort((a, b) => {
            var pathA = a.RelativePath;
            var pathB = b.RelativePath;

            var dirA = Path.GetDirectoryName(pathA) ?? string.Empty;
            var dirB = Path.GetDirectoryName(pathB) ?? string.Empty;

            var depthA = StringUtility.GetDirectoryDepth(dirA);
            var depthB = StringUtility.GetDirectoryDepth(dirB);

            var depthCompare = depthA.CompareTo(depthB);
            if (depthCompare != 0) {
                return depthCompare;
            }

            var dirCompare = string.Compare(dirA, dirB, StringComparison.OrdinalIgnoreCase);
            if (dirCompare != 0) {
                return dirCompare;
            }

            return b.Size.CompareTo(a.Size);
        });
    }

    public async Task Execute(RunnerCtx ctx)
    {
        var targetFile = Prompt.Select("Select file to download", m_installFiles, textSelector: MappedInstallFile.Format);
        await InstallFile(ctx, targetFile);
    }

    private async Task InstallFile(RunnerCtx ctx, MappedInstallFile file)
    {
        if (file.Type == MappedFileType.Containerless) {
            await InstallContainerlessFile(ctx, file);
            return;
        }

        Logger.Log($"Downloading {file.RelativePath} ({file.SizeStr})");
        Logger.Log($"   Install Path: {file.UserInstallPath}");
        Logger.Log($"   CKey: {Convert.ToHexStringLower(file.CKey.Span)}");

        var keys = new List<KeyValuePair<string, TACTArchiveBounds?>>();
        if (file.Locations == null || file.Locations.Length == 0) {
            if (ctx.EncodingManifest?.TryGetCKeyEntry(file.CKey.Span, out var ckeyEntry) == true) {
                Logger.Log($"   EM CKey: {ckeyEntry.CKey.ToString().ToLowerInvariant()}");
                var eKeys = ctx.EncodingManifest.GetEKeys(ckeyEntry).ToArray();
                foreach (var key in eKeys) {
                    Logger.Log($"       EM EKey: {key.ToString()}");
                    keys.Add(new KeyValuePair<string, TACTArchiveBounds?>(key.ToString(), null));
                }

                await InstallFileMerged(ctx, file, keys);
                return;
            }

            throw new ArgumentNullException(nameof(file), $"No CDN locations found for {file.RelativePath}");
        }

        Logger.Log($"   Locations: {file.Locations.Length}");
        foreach (var location in file.Locations) {
            Logger.Log($"       EKey: {location.EKey.ToString()}");
            Logger.Log($"       Archive: {location.Archive}");
            Logger.Log($"       Offset: 0x{location.Offset:X8}");
            Logger.Log($"       Size: 0x{location.Size:X8}");

            var targetKey = string.IsNullOrEmpty(location.Archive)
                ? location.EKey.ToString()
                : location.Archive;

            keys.Add(new KeyValuePair<string, TACTArchiveBounds?>(targetKey, new TACTArchiveBounds {
                Offset = location.Offset,
                Size = location.Size
            }));
        }

        await InstallFileMerged(ctx, file, keys);
        Logger.Log($"Downloaded {file.RelativePath} ({file.SizeStr}) to {file.UserInstallPath}");
    }

    private async Task InstallFileMerged(RunnerCtx ctx, MappedInstallFile file, List<KeyValuePair<string, TACTArchiveBounds?>> assets)
    {
        Logger.Log($"");
        Logger.Log($"[Manifest Downloader]");
        ulong offset = 0;
        foreach (var asset in assets) {
            Logger.Log($"   Key: {asset.Key}");

            PooledBuffer<byte>? buffer = null;
            try {
                buffer = await TACTAssetService.GetArchive(ctx.BuildInfo, asset.Key, asset.Value);
                if (buffer == null) {
                    throw new Exception($"Unable to download {file.RelativePath} ({file.SizeStr})");
                }

                if (buffer.Value.Span.IsBLTEEncoded()) {
                    var blte = new BLTE(buffer.Value.Span);
                    buffer.Value.Dispose();
                    buffer = blte.Data;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(file.UserInstallPath)
                                          ?? throw new InvalidOperationException($"Failed to get directory name for path: {file.UserInstallPath}"));

                var dataRes = buffer.Value.ReadOnlyMemory;
                await using var unarchivedWriteHandle = new FileStream(file.UserInstallPath, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None, (int) offset + dataRes.Length, FileOptions.Asynchronous);
                unarchivedWriteHandle.Seek((long) offset, SeekOrigin.Begin);
                await unarchivedWriteHandle.WriteAsync(dataRes);

                Logger.Log($"Downloaded {file.RelativePath} ({file.SizeStr}) to {file.UserInstallPath}");
                offset += (ulong) dataRes.Length;
            } catch (Exception ex) {
                Logger.Log($"Failed to download {file.RelativePath} ({file.SizeStr}) from key {asset.Key}: {ex.Message}");
            } finally {
                buffer?.Dispose();
            }
        }
    }

    private async Task InstallContainerlessFile(RunnerCtx ctx, MappedInstallFile file)
    {
        Logger.Log($"");
        Logger.Log($"[Manifest Downloader]");

        if (file.Locations is not { Length: 1 }) {
            throw new InvalidOperationException($"Containerless file {file.RelativePath} should have exactly one CDN location.");
        }

        var headerOwner = await FetchLocation(ctx, file.Locations[0]);
        using var header = UnwrapEHeader(headerOwner);
        var chunks = BLTE.ReadChunkHeaders(header.Span);

        long expectedContentSize = 0;
        foreach (var chunk in chunks) {
            expectedContentSize = checked(expectedContentSize + chunk.ContentSize);
        }

        if (expectedContentSize != file.Size) {
            throw new InvalidDataException($"File DB CSize {file.Size} does not match BLTE decoded chunks ({expectedContentSize}).");
        }

        Logger.Log($"[Containerless] {file.RelativePath}: {chunks.Length} chunk(s), {expectedContentSize} decoded bytes");

        var directory = Path.GetDirectoryName(file.UserInstallPath);
        if (directory is null) {
            throw new InvalidOperationException("Invalid output path.");
        }

        Directory.CreateDirectory(directory);

        try {
            using var wholeFileHash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            await using (var output = new FileStream(file.UserInstallPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920,
                             FileOptions.Asynchronous)) {
                for (var i = 0; i < chunks.Length; i++) {
                    var chunk = chunks[i];
                    var decodedLength = checked((int) chunk.ContentSize);
                    using var pool = new PooledBuffer<byte>(Math.Max(decodedLength, 1));
                    await DownloadAndDecodeChunk(ctx, chunk, i, pool.Memory);
                    wholeFileHash.AppendData(pool.ReadOnlySpan);
                    await output.WriteAsync(pool.ReadOnlyMemory);
                    Logger.Log($"  Chunk {i + 1}/{chunks.Length}: {decodedLength} bytes OK");
                }
            }

            var actualCKey = wholeFileHash.GetHashAndReset();
            if (file.CKey.Length != 16 || !actualCKey.AsSpan().SequenceEqual(file.CKey.Span)) {
                throw new InvalidDataException($"CKey mismatch: {Convert.ToHexString(actualCKey)}, expected  {Convert.ToHexString(file.CKey.Span)}.");
            }

            Logger.Log($"[Containerless] Verified: {file.UserInstallPath}");
        } catch {
            File.Delete(file.UserInstallPath);
            throw;
        }
    }

    private static async Task<PooledBuffer<byte>> FetchLocation(RunnerCtx ctx, CDNFileLocation location)
    {
        var objectName = string.IsNullOrEmpty(location.Archive)
            ? location.EKey.ToString()
            : location.Archive;

        TACTArchiveBounds? bounds = string.IsNullOrEmpty(location.Archive)
            ? null
            : new TACTArchiveBounds { Offset = location.Offset, Size = location.Size };

        var fetched = await TACTAssetService.GetArchive(ctx.BuildInfo, objectName, bounds);
        if (fetched is null) {
            throw new IOException($"CDN object unavailable: {objectName}");
        }

        return fetched.Value;
    }

    private static PooledBuffer<byte> UnwrapEHeader(PooledBuffer<byte> data)
    {
        var spn = data.Span;
        for (var layer = 0; layer < 4; layer++) {
            if (spn.IsEmpty) {
                throw new InvalidDataException("Empty E-header.");
            }

            if (spn.IsBLTEEncoded()) {
                if (spn.Length < 12) {
                    throw new InvalidDataException("Truncated E-header BLTE.");
                }

                var headerSize = BinaryPrimitives.ReadUInt32BigEndian(spn.Slice(4, 4));
                if (headerSize != 0 && headerSize == spn.Length) {
                    var result = new PooledBuffer<byte>(spn);
                    data.Dispose();
                    return result;
                }

                var newData = new BLTE(spn).Data;
                data.Dispose();
                data = newData;
                spn = data.Span;
                continue;
            }

            if (spn[0] == (byte) 'E') {
                ContainerCrypt.DecryptContainerlessInPlace(ref spn);
                continue;
            }

            break;
        }

        throw new InvalidDataException("E-header did not unwrap to a standalone BLTE chunk table.");
    }

    private async Task DownloadAndDecodeChunk(RunnerCtx ctx, BLTEChunk.ChunkHeader chunk, int chunkIndex, Memory<byte> destination)
    {
        if (ctx.EncodingManifest is null || ctx.IndexManifest is null) {
            throw new InvalidOperationException("Chunk lookup requires encoding and CDN indices.");
        }

        if (!ctx.EncodingManifest.TryGetCKeyEntry(chunk.Checksum, out var ckeyEntry)) {
            throw new FileNotFoundException($"Chunk {chunkIndex}: CKey lookup failed for {Convert.ToHexString(chunk.Checksum)}.");
        }

        Exception? lastError = null;

        var locations = new List<CDNFileLocation>();
        foreach (var eKey in ctx.EncodingManifest.GetEKeys(ckeyEntry)) {
            if (!ctx.IndexManifest.TryGetEntry(eKey, out var location)) {
                continue;
            }

            locations.Add(location);
        }

        foreach (var location in locations) {
            try {
                using var owner = await FetchLocation(ctx, location);
                DiagnoseEncryptedChunk(owner.Memory, chunk, chunkIndex);
                DecodeFetchedChunk(owner.Memory, chunk, chunkIndex, destination.Span);
                return;
            } catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or KeyNotFoundException) {
                lastError = ex;
                Logger.Log($"  Chunk {chunkIndex}: candidate failed: {ex.Message}");
            }
        }

        throw new IOException($"Chunk {chunkIndex}: no usable encoding found for {Convert.ToHexString(chunk.Checksum)}.",
            lastError);
    }

    private static void DecodeFetchedChunk(Memory<byte> fetched, BLTEChunk.ChunkHeader chunk, int chunkIndex, Span<byte> destination)
    {
        if (fetched.IsEmpty) {
            throw new InvalidDataException($"Chunk {chunkIndex} is empty.");
        }

        var data = fetched.Span;
        if (data.IsBLTEEncoded()) {
            DecodeOuterBlte(data, destination, chunkIndex);
            chunk.EnsureContentMatches(destination);
            //VerifyDecodedChunk(chunk, destination, chunkIndex);
            return;
        }

        if (data[0] == (byte) 'E') {
            ContainerCrypt.DecryptContainerlessInPlace(ref data, nonceIter: null);
        }

        if (chunk.Checksum.Length == 16) {
            Span<byte> actual = stackalloc byte[16];
            MD5.HashData(data, actual);

            if (!actual.SequenceEqual(chunk.Checksum)) {
                throw new InvalidDataException(
                    $"Chunk {chunkIndex} EKey mismatch. {Convert.ToHexString(actual)} expected {Convert.ToHexString(chunk.Checksum)}");
            }
        }

        if (data.IsBLTEEncoded()) {
            DecodeOuterBlte(data, destination, chunkIndex);
        } else if (data.IsBLTEEncodedChunk()) {
            BLTE.DecodeStandaloneChunk(data, destination, 0);
        } else if (data.Length == destination.Length) {
            data.CopyTo(destination);
        } else {
            throw new InvalidDataException($"Chunk {chunkIndex}: unknown payload format ({data.Length} bytes, first=0x{data[0]:X2}).");
        }

        chunk.EnsureContentMatches(destination);
    }

    private static void DecodeOuterBlte(Span<byte> encoded, Span<byte> destination, int chunkIndex)
    {
        if (encoded.Length < 8) {
            throw new InvalidDataException("Truncated nested BLTE.");
        }

        var headerSize = BinaryPrimitives.ReadUInt32BigEndian(encoded.Slice(4, 4));
        if (headerSize == 0) {
            BLTE.DecodeStandaloneChunk(encoded[8..], destination, 0);
            return;
        }

        using var blte = new BLTE(encoded);
        if (blte.Data.Length != destination.Length) {
            throw new InvalidDataException($"Chunk {chunkIndex} nested BLTE decoded {blte.Data.Length} but expected {destination.Length}.");
        }

        blte.Data.ReadOnlySpan.CopyTo(destination);
    }

    [Conditional("DEBUG")]
    private static void DiagnoseEncryptedChunk(Memory<byte> fetched, BLTEChunk.ChunkHeader chunk, int chunkIndex)
    {
        var raw = fetched.Span;
        Logger.Log($"[Chunk {chunkIndex}] Fetched: {raw.Length} bytes");
        Logger.Log($"  Encoded size expected: {chunk.EncodedSize}");
        Logger.Log($"  Decoded size expected: {chunk.ContentSize}");

        Span<byte> digest = stackalloc byte[16];
        MD5.HashData(raw, digest);

        Logger.Log($"  Expected MD5: {Convert.ToHexString(chunk.Checksum)}");
        Logger.Log($"  Fetched MD5:  {Convert.ToHexString(digest)}");
        Logger.Log($"  MD5 matches:  {digest.SequenceEqual(chunk.Checksum)}");

        if (raw.IsEmpty || raw[0] != (byte) 'E') {
            return;
        }

        var nameLength = raw.Length > 1 ? raw[1] : 0;
        if (nameLength != 8 || raw.Length < 11) {
            return;
        }

        int ivLength = raw[2 + nameLength];
        var cipherOffset = 3 + nameLength + ivLength;
        if (cipherOffset >= raw.Length) {
            return;
        }

        Logger.Log($"  Cipher: {(char) raw[cipherOffset]}");
        Logger.Log($"  IV: {Convert.ToHexString(raw.Slice(3 + nameLength, ivLength))}");
        foreach (var nonceIter in new int?[] { null, chunkIndex }) {
            var copy = fetched.ToArray();
            try {
                Span<byte> decrypted = copy;
                ContainerCrypt.DecryptContainerlessInPlace(ref decrypted, nonceIter: nonceIter);

                Logger.Log($"  Nonce index {nonceIter?.ToString() ?? "none"}: decrypted {decrypted.Length} bytes");
                Logger.Log($"    Prefix: {Convert.ToHexString(decrypted[..Math.Min(32, decrypted.Length)])}");
                if (!decrypted.IsEmpty) {
                    Logger.Log($"    First byte: 0x{decrypted[0]:X2}");
                }

                Logger.Log($"    BLTE: {decrypted.IsBLTEEncoded()}");
                Logger.Log($"    BLTE chunk: {decrypted.IsBLTEEncodedChunk()}");
            } catch (Exception ex) {
                Logger.Log($"  Nonce index {nonceIter?.ToString() ?? "none"}: {ex.Message}");
            } finally {
                CryptographicOperations.ZeroMemory(copy);
            }
        }
    }
}