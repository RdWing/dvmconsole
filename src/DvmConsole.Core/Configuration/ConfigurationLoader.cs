namespace DvmConsole.Core.Configuration;

public static class ConfigurationLoader
{
    public static ConsoleConfiguration Load(string path)
    {
        ConsoleConfiguration configuration = YamlConfigurationReader.Read(path);
        ConfigurationNormalizer.Normalize(configuration);
        ConfigurationPathHydrator.LoadOptionalAliases(configuration);
        return configuration;
    }

    public static IReadOnlyList<string> Validate(ConsoleConfiguration configuration)
        => ConfigurationValidator.Validate(configuration);

    public static string ResolvePath(ConsoleConfiguration configuration, string? configuredPath)
        => ConfigurationPathHydrator.ResolvePath(configuration, configuredPath);
}
