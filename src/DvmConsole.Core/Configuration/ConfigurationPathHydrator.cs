namespace DvmConsole.Core.Configuration;

internal static class ConfigurationPathHydrator
{
    public static string ResolvePath(ConsoleConfiguration configuration, string? configuredPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);

        if (Path.IsPathRooted(configuredPath))
            return Path.GetFullPath(configuredPath);

        string baseDirectory = configuration.SourcePath is null
            ? Directory.GetCurrentDirectory()
            : Path.GetDirectoryName(configuration.SourcePath) ?? Directory.GetCurrentDirectory();

        return Path.GetFullPath(Path.Combine(baseDirectory, configuredPath));
    }

    public static void LoadOptionalAliases(ConsoleConfiguration configuration)
    {
        foreach (SystemConfiguration system in configuration.Systems)
        {
            string aliasPath = ResolvePath(configuration, system.AliasPath);
            if (!File.Exists(aliasPath))
                continue;

            system.RidAlias = AliasFileLoader.Load(aliasPath);
        }
    }
}
