using System.Buffers;
using System.Text.Json.Nodes;
using BattleNGDP.CASC;
using BattleNGDP.Core;
using BattleNGDP.TACT;
using BattleNGDP.TACT.CDN;
using BattleNGDP.TACT.Data;
using BattleNGDP.TACT.Manifest;
using Sharprompt;

namespace BattleNGDP.Runner;

public interface IRunner
{
    string Name { get; }
    string Description { get; }
    int Priority { get; }

    public bool CanExecute(RunnerCtx ctx);
    public Task PreExecute(RunnerCtx ctx);
    public Task Execute(RunnerCtx ctx);

    public static string ConsoleString(IRunner runner) =>
        $"{runner.Name,-15} {runner.Description}";
}

public class RunnerCtx
{
    public ProductInfo ProductInfo { get; set; }
    public ProductBuildInfo? BuildInfo { get; set; }
    public IndexManifest? IndexManifest { get; set; }
    public EncodingManifest? EncodingManifest { get; set; }
    public TVFSManifest? TVFSManifest { get; set; }
    public DownloadManifest? DownloadManifest { get; set; }
    public BuildFileDBManifest? BuildFileDB { get; set; }

    public static async Task<RunnerCtx> Create(string product)
    {
        var productInfo = await ProductManager.GetProduct(product);
        if (productInfo.ProductBuilds.Count == 0) {
            throw new Exception($"No builds found for product {product}");
        }

        return new RunnerCtx {
            ProductInfo = productInfo
        };
    }

    public async Task Load(ProductBuildInfo buildInfo)
    {
        if (!await buildInfo.EnsureTACTPath()) {
            throw new ArgumentNullException($"Failed to obtain a TACT path for {buildInfo.TACTName} v{buildInfo.Version}");
        }

        if (!await buildInfo.EnsureArmadillo()) {
            throw new ArgumentNullException($"Failed to obtain an armadillo for {buildInfo.TACTName} v{buildInfo.Version}");
        }

        if (!await buildInfo.LoadConfigs()) {
            if (buildInfo.TACTBuildConfig == null) {
                throw new ArgumentNullException($"Failed to load configs for {buildInfo.TACTName} v{buildInfo.Version}");
            }

            Console.WriteLine($"[!!!] Failed to load some configs for {buildInfo.TACTName} v{buildInfo.Version}. Some features may not work.");
        }

        BuildInfo = buildInfo;

        Logger.Log($"Loading Manifests for {BuildInfo.TACTName} v{BuildInfo.Version}");

        Logger.Log("[*] Loading index manifest...");
        IndexManifest = await IndexManifest.LoadAsync(buildInfo);

        Logger.Log("[*] Loading encoding manifest...");
        EncodingManifest = await EncodingManifest.LoadAsync(buildInfo);

        Logger.Log("[*] Loading download manifest...");
        DownloadManifest = await DownloadManifest.LoadAsync(buildInfo);

        if (buildInfo.TACTBuildConfig is { VFSRoot: not null }) {
            Logger.Log("[*] Loading TVFS manifest...");
            TVFSManifest = await TVFSManifest.LoadAsync(buildInfo, IndexManifest);
        }

        if (buildInfo.TACTBuildConfig is { BuildFileDB: not null }) {
            Logger.Log("[*] Loading containerless mode...");
            BuildFileDB = await BuildFileDBManifest.LoadAsync(buildInfo, EncodingManifest, IndexManifest);
        }

        Logger.Log(" == Completed manifest loading! ==");
    }
}