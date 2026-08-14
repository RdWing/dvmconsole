using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DvmConsole.Core.Configuration;

public static class ConfigurationLoader
{
    public static ConsoleConfiguration Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The codeplug file was not found.", fullPath);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        using var reader = File.OpenText(fullPath);
        ConsoleConfiguration configuration = deserializer.Deserialize<ConsoleConfiguration>(reader)
            ?? throw new InvalidDataException("The codeplug file did not contain a configuration.");

        configuration.SourcePath = fullPath;
        Normalize(configuration);
        return configuration;
    }

    public static IReadOnlyList<string> Validate(ConsoleConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var errors = new List<string>();
        var systemNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (configuration.Systems.Count == 0)
            errors.Add("At least one system is required.");

        foreach (SystemConfiguration system in configuration.Systems)
        {
            if (string.IsNullOrWhiteSpace(system.Name))
                errors.Add("Every system must have a name.");
            else if (!systemNames.Add(system.Name))
                errors.Add($"System name '{system.Name}' is duplicated.");

            if (string.IsNullOrWhiteSpace(system.Address))
                errors.Add($"System '{system.Name}' must have an address.");
            if (system.Port is < 1 or > 65535)
                errors.Add($"System '{system.Name}' has an invalid port.");
        }

        var channelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ZoneConfiguration zone in configuration.Zones)
        {
            if (string.IsNullOrWhiteSpace(zone.Name))
                errors.Add("Every zone must have a name.");

            foreach (ChannelConfiguration channel in zone.Channels)
            {
                if (string.IsNullOrWhiteSpace(channel.Name))
                    errors.Add($"Zone '{zone.Name}' contains a channel without a name.");
                else if (!channelNames.Add(channel.Name))
                    errors.Add($"Channel name '{channel.Name}' is duplicated.");

                if (!systemNames.Contains(channel.System))
                    errors.Add($"Channel '{channel.Name}' references unknown system '{channel.System}'.");

                if (channel.Mode is not ("dmr" or "p25" or "nxdn"))
                    errors.Add($"Channel '{channel.Name}' has unsupported mode '{channel.Mode}'.");
            }
        }

        return errors;
    }

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

    private static void Normalize(ConsoleConfiguration configuration)
    {
        configuration.Systems ??= [];
        configuration.Zones ??= [];
        configuration.Groups ??= [];
        configuration.LegacyPatchGroups ??= [];

        foreach (SystemConfiguration system in configuration.Systems)
        {
            system.AliasPath = string.IsNullOrWhiteSpace(system.AliasPath)
                ? "./alias.yml"
                : system.AliasPath.Trim();
        }

        foreach (ZoneConfiguration zone in configuration.Zones)
        {
            zone.Channels ??= [];
            zone.WebStreams ??= [];
        }
    }
}
