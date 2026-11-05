using BattleNGDP.CASC;
using BattleNGDP.Core;
using BattleNGDP.TACT.CDN;
using BattleNGDP.TACT.Data;
using BattleNGDP.TACT.Manifest;
using Sharprompt;

namespace BattleNGDP.Runner;

public class RunnerInstallTVFSFiles : IRunner
{
    public string Name => "Install TVFS Files";
    public string Description => "Read and grab TVFS files.";
    public int Priority => 2;

    private struct MappedTVFSInstallFile
    {
        public Entry Entry { get; set; }
        public string SizeStr { get; set; }
        public string UserInstallPath { get; set; }
    }

    public bool CanExecute(RunnerCtx ctx) =>
        ctx.BuildInfo != null && ctx.BuildInfo.InstallManifest != null && ctx.TVFSManifest != null;

    public async Task PreExecute(RunnerCtx ctx) { }

    public async Task Execute(RunnerCtx ctx)
    {
        var targetPath = Prompt.Input<string>("File path to download");
        if (string.IsNullOrEmpty(targetPath)) {
            Console.WriteLine("No file path provided.");
            return;
        }

        if (!ctx.TVFSManifest!.Entries.TryGetValue(targetPath, out var tvfsEntry)) {
            Console.WriteLine($"File path '{targetPath}' not found in TVFS manifest.");
            return;
        }

        var tvfsMap = new MappedTVFSInstallFile {
            Entry = tvfsEntry,
            SizeStr = StringUtility.BytesToString(tvfsEntry.Size),
            UserInstallPath = Path.GetFullPath(Path.Join(Config.Root,
                $"{ctx.BuildInfo.TACTName}/Install/{ctx.BuildInfo.Version}/TVFS/{tvfsEntry.Path.Replace(':', Path.DirectorySeparatorChar)}"))
        };

        if (ctx.EncodingManifest?.TryGetCKeyEntry(tvfsMap.Entry.Spans[0].Container.CKey.Value, out var ckeyEntry) == true) {
            Logger.Log($"   EM CKey: {ckeyEntry.CKey.ToString().ToLowerInvariant()}");
            var eKeys = ctx.EncodingManifest.GetEKeys(ckeyEntry).ToArray();
            foreach (var key in eKeys) {
                Logger.Log($"       EM EKey: {key.ToString()}");
                //keys.Add(new KeyValuePair<string, TACTArchiveBounds?>(key.ToString(), null));
            }

            //await InstallFileMerged(ctx, file, keys);
            //return;
        }

        await InstallFile(ctx, tvfsMap);
    }

    private async Task InstallFile(RunnerCtx ctx, MappedTVFSInstallFile tvfsMap)
    {
        Logger.Log($"");
        Logger.Log($"[Manifest Downloader]");
        ulong offset = 0;
        foreach (var fileSpan in tvfsMap.Entry.Spans) {
            Logger.Log($"   CKey: {fileSpan.Container.CKey?.ToString()}");
            Logger.Log($"   Key: {fileSpan.Container.EKey.ToString()}");


            if (!TryResolveTVFSContainer(ctx, fileSpan.Container, out var keyOut, out var location)) {
                Console.WriteLine($"Failed to resolve TVFS container for {tvfsMap.Entry.Path} ({tvfsMap.SizeStr})");
                return;
            }

            string targetKey;
            TACTArchiveBounds? archiveBounds = null;
            if (location.Type == CDNSourceType.Archive) {
                targetKey = location.Archive ?? throw new Exception($"Failed to get archive key for {tvfsMap.Entry.Path} ({tvfsMap.SizeStr})");
                archiveBounds = new TACTArchiveBounds {
                    Offset = location.Offset,
                    Size = location.Size
                };
            } else {
                targetKey = keyOut.ToString();
            }

            PooledBuffer<byte>? buffer = null;
            try {
                buffer = await TACTAssetService.GetArchive(ctx.BuildInfo, targetKey, archiveBounds);
                if (buffer == null) {
                    throw new Exception($"Unable to download {tvfsMap.Entry.Path} ({tvfsMap.SizeStr})");
                }

                if (buffer.Value.Span.IsBLTEEncoded()) {
                    var blte = new BLTE(buffer.Value.Span);
                    buffer.Value.Dispose();
                    buffer = blte.Data;
                }

                var dataRes = buffer.Value.ReadOnlyMemory;
                if (fileSpan.Container.ContentSize != 0 && dataRes.Length != fileSpan.Container.ContentSize) {
                    Logger.Log($"   Container size mismatch: decoded={dataRes.Length} CFT={fileSpan.Container.ContentSize}");
                }

                var spanEnd = (ulong) fileSpan.Offset + fileSpan.Size;
                if (spanEnd > (ulong) dataRes.Length) {
                    throw new InvalidDataException(
                        $"TVFS span is outside decoded container: 0x{fileSpan.Offset:X} + 0x{fileSpan.Size:X} > 0x{dataRes.Length:X}");
                }

                var fileData = dataRes.Slice(checked((int) fileSpan.Offset), checked((int) fileSpan.Size));
                Directory.CreateDirectory(Path.GetDirectoryName(tvfsMap.UserInstallPath)
                                          ?? throw new InvalidOperationException(
                                              $"Failed to get directory name for file path: {tvfsMap.UserInstallPath}"));
                await using var unarchivedWriteHandle = new FileStream(tvfsMap.UserInstallPath, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None, (int) offset + dataRes.Length, FileOptions.Asynchronous);
                unarchivedWriteHandle.Seek((long) offset, SeekOrigin.Begin);
                await unarchivedWriteHandle.WriteAsync(fileData);

                Logger.Log($"Downloaded {tvfsMap.Entry.Path} ({tvfsMap.SizeStr}) to {tvfsMap.UserInstallPath}");
                offset += (ulong) fileData.Length;
            } catch (Exception ex) {
                Logger.Log(
                    $"Failed to download {tvfsMap.Entry.Path} ({tvfsMap.SizeStr}) from key {fileSpan.Container.EKey.ToString()}: {ex.Message}");
            } finally {
                buffer?.Dispose();
            }
        }
    }

    private static bool TryResolveTVFSContainer(RunnerCtx ctx, ContainerFile container, out TACTKey fullEKey, out CDNFileLocation location)
    {
        fullEKey = default;
        location = default;

        if (ctx.EncodingManifest == null ||
            ctx.IndexManifest == null) {
            return false;
        }

        if (container.CKey is not { } cKey) {
            return false;
        }

        if (!ctx.EncodingManifest.TryGetCKeyEntry(cKey, out var ckeyEntry)) {
            return false;
        }

        var partialEKey = container.EKey.ToString();

        foreach (var candidate in ctx.EncodingManifest.GetEKeys(ckeyEntry)) {
            var candidateString = candidate.ToString();

            if (!candidateString.StartsWith(partialEKey, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            if (!ctx.IndexManifest.TryGetEntry(candidate, out location)) {
                continue;
            }

            fullEKey = candidate;
            return true;
        }

        return false;
    }
}