using DvmConsole.Core.Configuration;

namespace DvmConsole.App;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("DvmConsole cross-platform bootstrap");
            Console.WriteLine("Usage: DvmConsole.App <path-to-codeplug.yml>");
            return 0;
        }

        ConsoleConfiguration configuration = ConfigurationLoader.Load(args[0]);
        IReadOnlyList<string> errors = ConfigurationLoader.Validate(configuration);

        Console.WriteLine($"Loaded {configuration.Systems.Count} system(s), {configuration.Zones.Count} zone(s).");
        if (errors.Count > 0)
        {
            Console.WriteLine("Configuration validation: failed");
            foreach (string error in errors)
                Console.WriteLine($"- {error}");

            return 1;
        }

        Console.WriteLine("Configuration validation: passed");

        return 0;
    }
}
