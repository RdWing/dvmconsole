using DvmConsole.Core.Configuration;

namespace DvmConsole.CodeplugValidator;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: DvmConsole.CodeplugValidator <path-to-codeplug.yml>");
            return args.Length == 0 ? 0 : 2;
        }

        try
        {
            ConsoleConfiguration configuration = ConfigurationLoader.Load(args[0]);
            IReadOnlyList<string> errors = ConfigurationLoader.Validate(configuration);
            Console.WriteLine(
                $"Loaded {configuration.Systems.Count} system(s), {configuration.Zones.Count} zone(s).");
            if (errors.Count == 0)
            {
                Console.WriteLine("Configuration validation: passed");
                return 0;
            }

            Console.Error.WriteLine("Configuration validation: failed");
            foreach (string error in errors)
                Console.Error.WriteLine($"- {error}");
            return 1;
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            FormatException or
            YamlDotNet.Core.YamlException)
        {
            Console.Error.WriteLine($"Unable to validate codeplug: {exception.Message}");
            return 2;
        }
    }
}
