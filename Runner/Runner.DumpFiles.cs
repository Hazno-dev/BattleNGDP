using BattleNGDP.CASC;
using BattleNGDP.Core;
using BattleNGDP.TACT.CDN;
using BattleNGDP.TACT.Manifest;
using Sharprompt;

namespace BattleNGDP.Runner;

public class RunnerDumpFiles : IRunner
{
    private struct MappedInstallFile
    {
        public InstallManifestFile ManifestFile { get; set; }
        public CDNFileLocation[]? Locations { get; set; }
        public string SizeStr { get; set; }
        public string UserInstallPath { get; set; }

        public static string Format(MappedInstallFile file)
        {
            if (file.Locations == null || file.Locations.Length == 0) {
                return $"{file.SizeStr,-14} {file.ManifestFile.Name,-64} [NOT FOUND]";
            }

            return $"{file.SizeStr,-14} {file.ManifestFile.Name,-64}";
        }
    }

    public string Name => "Dump Filelist";
    public string Description => "Read and dump install/TVFS filelist.";
    public int Priority => 100;

    private List<MappedInstallFile> m_installFiles = [];

    public bool CanExecute(RunnerCtx ctx) =>
        ctx.BuildInfo != null && ctx.BuildInfo.InstallManifest != null && ctx.IndexManifest != null;

    public async Task PreExecute(RunnerCtx ctx)
    {
        m_installFiles.Clear();
        var cdnLocationBuf = new List<CDNFileLocation>();
        foreach (var file in ctx.BuildInfo!.InstallManifest!.Files) {
            var map = new MappedInstallFile {
                ManifestFile = file,
                SizeStr = StringUtility.BytesToString(file.Size),
                UserInstallPath = Path.GetFullPath(Path.Join(Config.Root, $"{ctx.BuildInfo.TACTName}/Install/{ctx.BuildInfo.Version}/{file.Name}"))
            };

            if (!ctx.EncodingManifest.TryGetCKeyEntry(file.CKey.AsSpan(), out var ckeyEntry)) {
                //Logger.Log($"  CKey: {Convert.ToHexStringLower(file.ContentKey.Span)} -> NOT FOUND IN ENCODING MANIFEST");
                m_installFiles.Add(map);
                continue;
            }

            cdnLocationBuf.Clear();
            foreach (var eKey in ctx.EncodingManifest.GetEKeys(ckeyEntry)) {
                if (!ctx.EncodingManifest.TryGetEKeyEntry(eKey, out var eKeyEntry)) {
                    //Logger.Log($"  EKey: {eKeyEntry.EKey.ToString().ToLowerInvariant()} -> {eKeyEntry.FileSize} bytes ({eKeyEntry.ESpecIndex})");
                    if (!ctx.IndexManifest!.TryGetEntry(eKey, out var eKeylocation)) {
                        //Logger.Log($"  EKey: {eKey} -> NOT FOUND IN CDN INDEX");
                        continue;
                    }

                    cdnLocationBuf.Add(eKeylocation);
                    continue;
                }

                if (!ctx.IndexManifest!.TryGetEntry(eKeyEntry.EKey, out var location)) {
                    //Logger.Log($"  EKey: {eKey} -> NOT FOUND IN CDN INDEX");
                    continue;
                }

                cdnLocationBuf.Add(location);
            }

            if (cdnLocationBuf.Count > 0) {
                map.Locations = [.. cdnLocationBuf];
            }

            m_installFiles.Add(map);
        }

        m_installFiles.Sort((a, b) => {
            var pathA = a.ManifestFile.Name;
            var pathB = b.ManifestFile.Name;

            var dirA = Path.GetDirectoryName(pathA) ?? string.Empty;
            var dirB = Path.GetDirectoryName(pathB) ?? string.Empty;

            // Root files first, then files 1 directory deep, then 2, etc.
            var depthA = StringUtility.GetDirectoryDepth(dirA);
            var depthB = StringUtility.GetDirectoryDepth(dirB);

            var depthCompare = depthA.CompareTo(depthB);
            if (depthCompare != 0) {
                return depthCompare;
            }

            // Keep files belonging to the same directory together.
            var dirCompare = string.Compare(dirA,
                dirB,
                StringComparison.OrdinalIgnoreCase);

            if (dirCompare != 0) {
                return dirCompare;
            }

            // Same directory: largest file first.
            return b.ManifestFile.Size.CompareTo(a.ManifestFile.Size);

            /*if (pathCompare == 0) {
                var folderNameCompare = string.Compare(dirA, dirB, StringComparison.OrdinalIgnoreCase);
                if (folderNameCompare != 0) {
                    return folderNameCompare;
                }

                return -a.ManifestFile.Size.CompareTo(b.ManifestFile.Size);
            }

            return pathCompare;*/
        });
    }

    public async Task Execute(RunnerCtx ctx)
    {
        var fullPath = Path.GetFullPath(Path.Join(Config.Root, $"{ctx.BuildInfo.TACTName}/Install/{ctx.BuildInfo.Version}/filelist.txt"));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ??
                                  throw new InvalidOperationException($"Failed to get directory name for file path: {fullPath}"));
        Logger.Log($"Writing files to {fullPath}");

        await using var fileHandle = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var streamWriter = new StreamWriter(fileHandle);
        await streamWriter.WriteLineAsync($"======================================");
        await streamWriter.WriteLineAsync($" BUILD FILES");
        await streamWriter.WriteLineAsync($"");
        await streamWriter.WriteLineAsync($" {ctx.BuildInfo.TACTBuildConfig.Name} {ctx.BuildInfo.TACTBuildConfig.Comments}");
        await streamWriter.WriteLineAsync($" Version {ctx.BuildInfo.Version} {ctx.BuildInfo.TACTBuildConfig.Branch}");
        await streamWriter.WriteLineAsync($"======================================");
        await streamWriter.WriteLineAsync($"");
        await streamWriter.WriteLineAsync($"==== INSTALL FILES ====");
        await streamWriter.WriteLineAsync($" Total Files: {m_installFiles.Count}");
        await streamWriter.WriteLineAsync($"");
        foreach (var file in m_installFiles) {
            await streamWriter.WriteLineAsync($"{file.SizeStr,-14} {file.ManifestFile.Name}");
        }

        await streamWriter.WriteLineAsync($"");
        await streamWriter.WriteLineAsync($"==== INSTALL FILES END ====");
        await streamWriter.WriteLineAsync($"");

        if (ctx.TVFSManifest != null) {
            ulong totalBytes = 0;
            await streamWriter.WriteLineAsync($"");
            await streamWriter.WriteLineAsync($"==== TVFS FILES ====");
            await streamWriter.WriteLineAsync($" Total Files: {ctx.TVFSManifest.Entries.Count}");
            await streamWriter.WriteLineAsync($"");
            foreach (var entry in ctx.TVFSManifest.Entries) {
                await streamWriter.WriteLineAsync($"{entry.Value.Size,-14} {entry.Key}");
                totalBytes += entry.Value.Size;
            }

            var totalBytesStr = StringUtility.BytesToString(totalBytes);
            await streamWriter.WriteLineAsync($"");
            await streamWriter.WriteLineAsync($" Total Size {totalBytesStr}");
            await streamWriter.WriteLineAsync($"==== TVFS FILES END ====");
            await streamWriter.WriteLineAsync($"");
        }
    }
}