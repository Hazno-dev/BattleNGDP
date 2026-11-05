using System.CommandLine;
using System.Reflection;
using BattleNGDP.Core;
using BattleNGDP.TACT.CDN;
using Sharprompt;

namespace BattleNGDP.Runner;

public class RunnerRoot
{
    private readonly List<IRunner> m_runners;

    public RunnerRoot()
    {
        m_runners = [];
        var leafTypes = Assembly.GetExecutingAssembly().GetTypes()
            .Where(t => typeof(IRunner).IsAssignableFrom(t)
                        && t is { IsInterface: false, IsAbstract: false })
            .ToList();

        foreach (var type in leafTypes) {
            if (Activator.CreateInstance(type) is IRunner instance) {
                m_runners.Add(instance);
            }
        }

        m_runners.Sort((a, b) => a.Priority.CompareTo(b.Priority));
    }

    public async Task Run(string[] args)
    {
        var tactArgument = new Argument<string>("TactID") {
            Description = "Product TACT ID (i.e. \"pro\"."
        };

        var headOnly = new Option<bool>("--fast", "-f") {
            Required = false,
            DefaultValueFactory = _ => false
        };

        RootCommand rootCommand = new("BattleNGDP - Parser");
        rootCommand.Arguments.Add(tactArgument);
        rootCommand.Options.Add(headOnly);

        var res = rootCommand.Parse(args);
        var product = res.GetValue(tactArgument);
        if (product is null) {
            Console.WriteLine("No product found");
            return;
        }

        TACTAssetService.HeadOnly = res.GetValue(headOnly);

        var ctx = await RunnerCtx.Create(product);
        var validRunners = new List<IRunner>();
        try {
            while (true) {
                PopulateValidRunners(ctx, validRunners);
                var targetRunner = Prompt.Select("Select runner", validRunners, textSelector: IRunner.ConsoleString);
                await Run(ctx, targetRunner);
            }
        } catch (PromptCanceledException) {
            Logger.Log("Prompt canceled, exiting...");
        } catch (Exception ex) {
            Logger.Log($"Error: {ex}");
        }

        Console.WriteLine("Press any key to exit...");
        Console.ReadLine();
    }


    public static async Task Run(RunnerCtx ctx, IRunner runner)
    {
        await runner.PreExecute(ctx);
        await runner.Execute(ctx);
    }

    private void PopulateValidRunners(RunnerCtx ctx, List<IRunner> validList)
    {
        validList.Clear();
        foreach (var runner in m_runners) {
            if (runner.CanExecute(ctx)) {
                validList.Add(runner);
            }
        }
    }
}