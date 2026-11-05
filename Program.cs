using System.CommandLine;
using System.Reflection;
using BattleNGDP.Runner;

var root = new RunnerRoot();
await root.Run(args);

/*var tactArgument = new Argument<string>("TactID") {
    Description = "Product TACT ID (i.e. \"pro\"."
};

RootCommand rootCommand = new("BattleNGDP - Parser");
rootCommand.Arguments.Add(tactArgument);

var res = rootCommand.Parse(args);
var product = res.GetValue(tactArgument);
if (product is null) {
    Console.WriteLine("No product found");
    return;
}

var instances = new List<IRunner>();
var leafTypes = Assembly.GetExecutingAssembly().GetTypes()
    .Where(t => typeof(IRunner).IsAssignableFrom(t) 
                && t is { IsInterface: false, IsAbstract: false })
    .ToList();

foreach (var type in leafTypes) {
    if (Activator.CreateInstance(type) is IRunner instance) {
        instances.Add(instance);
    }
}

var ctx = await RunnerCtx.Create(product);
foreach (var instance in instances) {
    if (instance.CanExecute())
}

var runner = new RunnerTACTProduct();
await runner.Run(product);

// 3. Execute actions on the instances
foreach (var item in instances)
{
    item.DoSomething();
}*/