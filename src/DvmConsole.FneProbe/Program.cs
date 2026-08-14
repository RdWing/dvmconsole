using DvmConsole.Core.Configuration;
using DvmConsole.FneClient;

namespace DvmConsole.FneProbe;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length is < 1 or > 2 || args[0] is "-h" or "--help")
        {
            Console.WriteLine("Usage: DvmConsole.FneProbe <codeplug.yml> [duration-seconds]");
            Console.WriteLine("This utility intentionally opens live FNE connections and is never run by the desktop app.");
            return args.Length == 0 || args[0] is "-h" or "--help" ? 0 : 2;
        }

        if (!int.TryParse(args.ElementAtOrDefault(1) ?? "10", out int durationSeconds) || durationSeconds is < 1 or > 300)
        {
            Console.Error.WriteLine("Duration must be an integer between 1 and 300 seconds.");
            return 2;
        }

        ConsoleConfiguration configuration = ConfigurationLoader.Load(args[0]);
        IReadOnlyList<string> errors = ConfigurationLoader.Validate(configuration);
        if (errors.Count > 0)
        {
            foreach (string error in errors)
                Console.Error.WriteLine(error);
            return 1;
        }

        var connections = configuration.Systems
            .Select(FneConnectionOptions.FromConfiguration)
            .Select(options => options with { EnableDiagnostics = true })
            .Select(options => new FneConnection(options))
            .ToArray();

        foreach (FneConnection connection in connections)
            connection.StatusChanged += HandleStatusChanged;

        bool reachedConnected = false;
        foreach (FneConnection connection in connections)
            connection.StatusChanged += (_, status) => reachedConnected |= status.State == FneConnectionState.Connected;

        Console.WriteLine($"Starting {connections.Length} live FNE connection(s) for {durationSeconds} seconds.");
        foreach (FneConnection connection in connections)
        {
            try
            {
                await connection.StartAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[{connection.Status.Name}] start failed: {exception.Message}");
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(durationSeconds)).ConfigureAwait(false);

        foreach (FneConnection connection in connections)
        {
            try
            {
                await connection.StopAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[{connection.Status.Name}] stop failed: {exception.Message}");
            }
        }

        foreach (FneConnection connection in connections)
            connection.StatusChanged -= HandleStatusChanged;

        return reachedConnected ? 0 : 1;
    }

    private static void HandleStatusChanged(object? sender, FneConnectionStatus status)
    {
        Console.WriteLine($"[{status.Name}] {status.State}: {status.Message}");
    }
}
