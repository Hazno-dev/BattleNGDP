using BattleNGDP.Core;
using Sharprompt;

namespace BattleNGDP.Runner;

public class RunnerProductSelector : IRunner
{
    public string Name => "Build Selector";
    public string Description => "Select a product build";
    public int Priority => 0;

    private List<ProductBuildInfo> m_allBuilds = [];
    
    public bool CanExecute(RunnerCtx ctx)
    {
        return ctx.BuildInfo == null;
    }

    public Task PreExecute(RunnerCtx ctx)
    {
        m_allBuilds.Clear();
        m_allBuilds.AddRange(ctx.ProductInfo.ProductBuilds.Aggregate(new List<ProductBuildInfo>(), (list, build) => {
            list.AddRange(build.Value);
            return list;
        }));
        
        m_allBuilds.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        return Task.CompletedTask;
    }

    public async Task Execute(RunnerCtx ctx)
    {
        var targetBuild = Prompt.Select("Select product build", m_allBuilds, textSelector:build => build.CondensedString());
        if (string.IsNullOrEmpty(targetBuild.TACTName)) {
            targetBuild.TACTName = ctx.ProductInfo.TACTName;
            ProductStorage.Save();
        }
        
        await ctx.Load(targetBuild);
    }
}