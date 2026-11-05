using BattleNGDP.Core;
using BattleNGDP.TACT.CDN;
using Sharprompt;

namespace BattleNGDP.Runner;

public class RunnerBulkChecker : IRunner
{
    public string Name => "Build Checker";
    public string Description => "Check all stored builds for product";
    public int Priority => 100;

    private List<ProductBuildInfo> m_allBuilds = [];

    public bool CanExecute(RunnerCtx ctx) =>
        ctx.BuildInfo == null;

    public Task PreExecute(RunnerCtx ctx)
    {
        m_allBuilds.Clear();
        m_allBuilds.AddRange(ctx.ProductInfo.ProductBuilds.Aggregate(new List<ProductBuildInfo>(), (list, build) => {
            list.AddRange(build.Value);
            return list;
        }));

        m_allBuilds.Sort((a, b) => b.Version.CompareTo(a.Version));
        return Task.CompletedTask;
    }

    public async Task Execute(RunnerCtx ctx)
    {
        var oldHeadOnly = TACTAssetService.HeadOnly;
        TACTAssetService.HeadOnly = true;
        foreach (var build in m_allBuilds) {
            try {
                await build.EnsureTACTPath();
                await build.EnsureArmadillo();
                await build.LoadConfigs();
            } catch { }
        }

        TACTAssetService.HeadOnly = oldHeadOnly;
    }
}