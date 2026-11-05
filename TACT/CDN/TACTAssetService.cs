using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using BattleNGDP.CASC;
using BattleNGDP.Core;
using BattleNGDP.TACT.Data;
using Org.BouncyCastle.OpenSsl;

namespace BattleNGDP.TACT.CDN;

public struct TACTArchiveBounds
{
    public ulong Offset { get; set; }
    public ulong Size { get; set; }
}

public static class TACTAssetService
{
    private static readonly Uri[] s_baseCDNs = [
        new("http://level3.blizzard.com/?maxhosts=4"),
        new("http://us.cdn.blizzard.com/?maxhosts=4"),
        new("http://eu.cdn.blizzard.com/?maxhosts=4"),
        new("http://blzdist-d4.necdn.leihuo.netease.com?maxhosts=6"),
        new("http://blzdist-phx.necdn.leihuo.netease.com"),
        new("http://kr.cdn.blizzard.com/?maxhosts=4"),
        new("http://blizzard.gcdn.cloudn.co.kr?maxhosts=4"),
        new("https://level3.ssl.blizzard.com?maxhosts=4&fallback=1")
    ];

    private static readonly HttpClient s_client;

    private static readonly TimeSpan s_headTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_getTimeout = TimeSpan.FromSeconds(30);

    //Skips CDN queries if HEAD fails.
    public static bool HeadOnly { get; set; }

    static TACTAssetService()
    {
        var handler = new HttpClientHandler {
            AllowAutoRedirect = true,
            CheckCertificateRevocationList = false
        };

        s_client = new HttpClient(handler) {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public static async Task<PooledBuffer<byte>?> GetConfig(ProductBuildInfo buildInfo, string hash) =>
        await Get($"{buildInfo.TACTPath}/config",
            $"{buildInfo.TACTName}/Config",
            hash,
            buildInfo.Armadillo,
            null);

    public static async Task<PooledBuffer<byte>?> GetData(ProductBuildInfo buildInfo, string hash) =>
        await Get($"{buildInfo.TACTPath}/data",
            $"{buildInfo.TACTName}/Data",
            hash,
            buildInfo.Armadillo,
            null);

    public static async Task<PooledBuffer<byte>?> GetDataBLTE(ProductBuildInfo buildInfo, string hash) =>
        await Get($"{buildInfo.TACTPath}/data",
            $"{buildInfo.TACTName}/Data",
            hash,
            buildInfo.Armadillo,
            null,
            true);

    public static async Task<PooledBuffer<byte>?> GetProductConfig(ProductBuildInfo buildInfo, string hash) =>
        await Get(buildInfo.ProductConfigPath ?? "tpr/configs/data",
            $"{buildInfo.TACTName}/ProductConfig",
            hash,
            null,
            null);

    public static async Task<PooledBuffer<byte>?> GetArchive(ProductBuildInfo buildInfo, string hash, TACTArchiveBounds? bounds) =>
        await Get($"{buildInfo.TACTPath}/data",
            $"{buildInfo.TACTName}/Archive",
            hash,
            buildInfo.Armadillo,
            bounds);

    public static async Task<PooledBuffer<byte>?> GetArchiveBLTE(ProductBuildInfo buildInfo, string hash, TACTArchiveBounds? bounds) =>
        await Get($"{buildInfo.TACTPath}/data",
            $"{buildInfo.TACTName}/Archive",
            hash,
            buildInfo.Armadillo,
            bounds,
            true);

    public static async Task<PooledBuffer<byte>?> GetArchiveIndex(ProductBuildInfo buildInfo, string hash) =>
        await Get($"{buildInfo.TACTPath}/data",
            $"{buildInfo.TACTName}/Indices",
            $"{hash}.index",
            buildInfo.Armadillo,
            null);

    public static async Task<PooledBuffer<byte>?> Get(string path, string localPath, string hash, string? armadillo, TACTArchiveBounds? bounds,
        bool parseBlte = false)
    {
        var hashSpan = hash.AsSpan();
        var hashPath = $"{hashSpan[..2]}/{hashSpan[2..4]}/{hashSpan}";
        localPath = Path.GetFullPath(Path.Join(Config.Root, $"{localPath}/{hashPath}"));
        var result = await GetLocal(localPath);
        if (result == null) {
            result = await GetRemote($"{path}/{hashPath}", bounds);
            if (result == null) {
                return null;
            }

            if (!bounds.HasValue) {
                Directory.CreateDirectory(Path.GetDirectoryName(localPath) ??
                                          throw new InvalidOperationException($"Failed to get directory name for file path: {localPath}"));
                await using var writeHandle = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, result.Value.Length,
                    FileOptions.Asynchronous);
                await writeHandle.WriteAsync(result.Value.ReadOnlyMemory);
            }
        }

        if (!string.IsNullOrEmpty(armadillo) && !Armadillo.Decrypt(result.Value, armadillo, hash, bounds?.Offset ?? 0)) {
            throw new IOException($"Armadillo decryption failed: {armadillo}");
        }

        if (!parseBlte) {
            return result;
        }

        if (result.Value.Span.IsBLTEEncoded()) {
            var installBlte = new BLTE(result.Value.Span);
            result.Value.Dispose();
            result = installBlte.Data;
        }

        return result;
    }

    private static async Task<PooledBuffer<byte>?> GetLocal(string localPath)
    {
        if (!File.Exists(localPath)) {
            return null;
        }

        await using var handle = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var rented = ArrayPool<byte>.Shared.Rent(checked((int) handle.Length));
        int bytesRead, totalBytesRead = 0;
        while ((bytesRead = await handle.ReadAsync(rented.AsMemory(totalBytesRead))) > 0) {
            totalBytesRead += bytesRead;
        }

        return PooledBuffer<byte>.FromRental(rented, totalBytesRead);
    }

    private static async Task<PooledBuffer<byte>?> GetRemote(string remotePath, TACTArchiveBounds? bounds)
    {
        for (var i = 0; i < s_baseCDNs.Length; i++) {
            var cdn = s_baseCDNs[i];
            var uri = new Uri($"{cdn.Scheme}://{cdn.Host}/{remotePath}{cdn.Query}");

            bool headPass;
            if (!(headPass = await PingUrl(uri))) {
                Logger.Log($"CDN {uri.AbsoluteUri} is unreachable via HEAD.");
                if (HeadOnly) {
                    continue;
                }
            }

            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (bounds.HasValue) {
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue((long) bounds.Value.Offset,
                    (long) (bounds.Value.Offset + bounds.Value.Size - 1));
            }

            try {
                using var cts = headPass ? null : new CancellationTokenSource(s_getTimeout);
                using var res = await s_client.SendAsync(request, cts?.Token ?? CancellationToken.None);
                if (!res.IsSuccessStatusCode) {
                    continue;
                }

                if (res.Content.Headers.ContentLength is null or 0) {
                    Console.WriteLine($"{request.RequestUri?.AbsoluteUri} - No content returned.");
                    continue;
                }

                var resStream = await res.Content.ReadAsStreamAsync(cts?.Token ?? CancellationToken.None);
                var rented = ArrayPool<byte>.Shared.Rent(checked((int) res.Content.Headers.ContentLength.Value));
                int bytesRead, totalBytesRead = 0;
                while ((bytesRead = await resStream.ReadAsync(rented.AsMemory(totalBytesRead), cts?.Token ?? CancellationToken.None)) > 0) {
                    totalBytesRead += bytesRead;
                }

                return PooledBuffer<byte>.FromRental(rented, totalBytesRead);
            } catch (TaskCanceledException) {
                Logger.Log($"Timed out when attempting to GET {request.RequestUri?.AbsoluteUri}");
            } catch (Exception ex) {
                Logger.Log($"Failed to fetch {request.RequestUri?.AbsoluteUri}: {ex.Message}");
            }
        }

        return null;
    }

    private static async Task<bool> PingUrl(Uri target)
    {
        try {
            using var cts = new CancellationTokenSource(s_headTimeout);
            var request = new HttpRequestMessage(HttpMethod.Head, target);
            var response = await s_client.SendAsync(request, cts.Token);
            return response.IsSuccessStatusCode;
        } catch (TaskCanceledException) {
            return false;
        }
    }
}