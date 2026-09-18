using IS74Wifi.Core;

namespace IS74Wifi.App;

internal static class Program
{
    private const string MigrationStage = "csharp-skeleton";

    [STAThread]
    private static int Main(string[] args)
    {
        var command = args.FirstOrDefault()?.Trim().ToLowerInvariant() ?? "menu";

        // Agent mode intentionally never acquires a console. The real agent loop
        // will be introduced only after its state-machine contracts are ported.
        if (command == "agent")
        {
            return 3;
        }

        ConsoleSession.EnsureInteractiveConsole();

        return command switch
        {
            "version" or "--version" or "-v" => PrintVersion(),
            "help" or "--help" or "-h" => PrintHelp(),
            "contract" => PrintContract(),
            "menu" => PrintMigrationNotice(),
            _ => UnknownCommand(command)
        };
    }

    private static int PrintVersion()
    {
        Console.WriteLine($"IS74Wifi {MigrationStage}");
        return 0;
    }

    private static int PrintHelp()
    {
        Console.WriteLine("IS74Wifi C# migration skeleton");
        Console.WriteLine("Commands: version, help, contract");
        Console.WriteLine("The PowerShell runtime remains the production/reference client during migration.");
        return 0;
    }

    private static int PrintContract()
    {
        Console.WriteLine($"SSID prefix: {ProtocolContract.CampusSsidPrefix}");
        Console.WriteLine($"Automatic stepOne limit: {ProtocolContract.MaxAutomaticStepOneAttempts}");
        Console.WriteLine($"Push poll offsets (ms): {string.Join(",", ProtocolContract.PushPollOffsetsMilliseconds.ToArray())}");
        return 0;
    }

    private static int PrintMigrationNotice()
    {
        Console.WriteLine("C# migration is in progress.");
        Console.WriteLine("Use the current PowerShell release for real Wi-Fi authorization until parity is reached.");
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        return 2;
    }
}
