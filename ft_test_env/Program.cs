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

Console.WriteLine("File Tunnel — Linux + Windows test environment orchestrator");
Console.WriteLine($"Working dir: {config.WorkingDir}");
Console.WriteLine($"Linux nodes: {string.Join(", ", config.Nodes.Select(n => $"{n.Name} ({n.Ip})"))}");
if (config.WindowsGold.Enabled)
    Console.WriteLine($"Windows clients: {string.Join(", ", config.WindowsNodes.Select(n => $"{n.CloneName} ({n.Ip})"))}  [gold {config.WindowsGold.GoldVmName} @ {config.WindowsGold.SourceIp}]");
if (config.WindowsServer.Enabled)
    Console.WriteLine($"Windows server: {config.WindowsServer.VmName} ({config.WindowsServer.Ip})  [hand-built, distinct SID]");
if (config.MacEmulator.Enabled)
    Console.WriteLine($"Android emulators (Mac {config.MacEmulator.Host}): {config.MacEmulator.Serial} + {config.MacEmulator.SecondSerial} (both bridged, sequential launch)  [set up in prep, launched at bring-up]");

while (true)
{
    Console.WriteLine();
    Console.WriteLine("==================================================");
    Console.WriteLine("  1) One-time prep (idempotent)");
    Console.WriteLine("  2) Bring up environment for a test run");
    Console.WriteLine("  3) Bring up a single node");
    Console.WriteLine("  4) Teardown (power off all nodes)");
    Console.WriteLine("  5) Check Linux services");
    Console.WriteLine("  6) Check Windows nodes");
    Console.WriteLine("  7) Check gold image readiness");
    Console.WriteLine("  8) Reboot a Windows machine (client clone or the server VM) to clear tiring");
    Console.WriteLine("  9) Bring up a single Windows client node (finish/repair a partial bring-up)");
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
            case "7":
                orchestrator.CheckGold();
                break;
            case "8":
                RebootMenu(config, orchestrator);
                break;
            case "9":
                var winNodeUp = PromptForWindowsNode(config);
                if (winNodeUp != null) orchestrator.BringUpWindowsNode(winNodeUp);
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

static WindowsNodeConfig? PromptForWindowsNode(EnvConfig config)
{
    if (!config.WindowsGold.Enabled || config.WindowsNodes.Count == 0)
    {
        Console.WriteLine("No Windows nodes configured.");
        return null;
    }

    Console.WriteLine("Which Windows node?");
    for (var i = 0; i < config.WindowsNodes.Count; i++)
    {
        var n = config.WindowsNodes[i];
        Console.WriteLine($"  {i + 1}) {n.CloneName} ({n.Ip}) [{n.Role}]");
    }
    Console.Write("Choose: ");

    if (int.TryParse(Console.ReadLine()?.Trim(), out var idx) && idx >= 1 && idx <= config.WindowsNodes.Count)
    {
        return config.WindowsNodes[idx - 1];
    }

    Console.WriteLine("Invalid selection.");
    return null;
}

// Reboot picker for menu 8: lists the client clones AND the hand-built server VM, and dispatches to the
// right orchestrator call (RebootNode vs RebootServer) since they are different kinds of machine.
static void RebootMenu(EnvConfig config, Orchestrator orchestrator)
{
    var listClients = config.WindowsGold.Enabled && config.WindowsNodes.Count > 0;
    var hasServer = config.WindowsServer.Enabled;
    if (!listClients && !hasServer)
    {
        Console.WriteLine("No Windows machines configured.");
        return;
    }

    Console.WriteLine("Which Windows machine to reboot?");
    var clientCount = listClients ? config.WindowsNodes.Count : 0;
    for (var i = 0; i < clientCount; i++)
    {
        var n = config.WindowsNodes[i];
        Console.WriteLine($"  {i + 1}) {n.CloneName} ({n.Ip}) [{n.Role}]");
    }
    if (hasServer)
        Console.WriteLine($"  {clientCount + 1}) {config.WindowsServer.VmName} ({config.WindowsServer.Ip}) [server]");
    Console.Write("Choose: ");

    if (int.TryParse(Console.ReadLine()?.Trim(), out var idx) && idx >= 1)
    {
        if (idx <= clientCount) orchestrator.RebootNode(config.WindowsNodes[idx - 1]);
        else if (hasServer && idx == clientCount + 1) orchestrator.RebootServer();
        else Console.WriteLine("Invalid selection.");
        return;
    }

    Console.WriteLine("Invalid selection.");
}
