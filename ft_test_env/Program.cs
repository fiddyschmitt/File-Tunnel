using ft_test_env;
using ft_test_env.Config;
using Microsoft.Extensions.Configuration;

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddUserSecrets(typeof(Program).Assembly, optional: true)
    .Build();

var config = configuration.Get<EnvConfig>() ?? new EnvConfig();
var orchestrator = new Orchestrator(config);

Console.WriteLine("File Tunnel — Linux test environment orchestrator");
Console.WriteLine($"Working dir: {config.WorkingDir}");
Console.WriteLine($"Nodes: {string.Join(", ", config.Nodes.Select(n => $"{n.Name} ({n.Ip})"))}");

while (true)
{
    Console.WriteLine();
    Console.WriteLine("==================================================");
    Console.WriteLine("  1) One-time prep (idempotent)");
    Console.WriteLine("  2) Bring up environment for a test run");
    Console.WriteLine("  3) Bring up a single node");
    Console.WriteLine("  4) Teardown (power off all nodes)");
    Console.WriteLine("  5) Check Linux services");
    Console.WriteLine("  6) Check Windows services");
    Console.WriteLine("  0) Exit");
    Console.WriteLine("==================================================");
    Console.Write("Choose: ");

    var choice = Console.ReadLine()?.Trim();
    Console.WriteLine();

    try
    {
        switch (choice)
        {
            case "1":
                orchestrator.Prep();
                break;
            case "2":
                orchestrator.BringUpAll();
                break;
            case "3":
                var node = PromptForNode(config);
                if (node != null) orchestrator.BringUpNode(node);
                break;
            case "4":
                orchestrator.Teardown();
                break;
            case "5":
                orchestrator.CheckLinux();
                break;
            case "6":
                orchestrator.CheckWindows();
                break;
            case "0":
            case "q":
            case null:
                return;
            default:
                Console.WriteLine("Unknown choice.");
                break;
        }
    }
    catch (Exception ex)
    {
        var original = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Unexpected error: {ex.Message}");
        Console.ForegroundColor = original;
    }
}

static NodeConfig? PromptForNode(EnvConfig config)
{
    Console.WriteLine("Which node?");
    for (var i = 0; i < config.Nodes.Count; i++)
    {
        var n = config.Nodes[i];
        Console.WriteLine($"  {i + 1}) {n.Name} ({n.Ip}){(n.IsServer ? " [server]" : "")}");
    }
    Console.Write("Choose: ");

    if (int.TryParse(Console.ReadLine()?.Trim(), out var idx) && idx >= 1 && idx <= config.Nodes.Count)
    {
        return config.Nodes[idx - 1];
    }

    Console.WriteLine("Invalid selection.");
    return null;
}
