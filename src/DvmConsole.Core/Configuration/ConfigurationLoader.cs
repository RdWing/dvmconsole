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
        LoadOptionalAliases(configuration);
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
            if (system.TransportEncryptionMode is not ("auto" or "ecb" or "cbc"))
                errors.Add($"System '{system.Name}' has unsupported transport encryption mode '{system.TransportEncryptionMode}'.");
        }

        var channelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var webStreamNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ZoneConfiguration zone in configuration.Zones)
        {
            if (string.IsNullOrWhiteSpace(zone.Name))
                errors.Add("Every zone must have a name.");

            foreach (ChannelConfiguration channel in zone.Channels)
            {
                string channelName = channel.Name?.Trim() ?? string.Empty;
                string channelSystem = channel.System?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(channel.Name))
                    errors.Add($"Zone '{zone.Name}' contains a channel without a name.");
                else if (!channelNames.Add($"{channelSystem}\u001F{channelName}"))
                    errors.Add($"Channel name '{channel.Name}' is duplicated in system '{channel.System}'.");

                if (!systemNames.Contains(channelSystem))
                    errors.Add($"Channel '{channel.Name}' references unknown system '{channel.System}'.");

                if (channel.Mode is not ("dmr" or "p25" or "nxdn" or "analog"))
                    errors.Add($"Channel '{channel.Name}' has unsupported mode '{channel.Mode}'.");

                if (!uint.TryParse(channel.Tgid, out uint destinationId) || destinationId == 0)
                    errors.Add($"Channel '{channel.Name}' must have a non-zero numeric destination ID.");

                if (channel.Mode == "dmr" && channel.Slot is < 1 or > 2)
                    errors.Add($"DMR channel '{channel.Name}' must use slot 1 or 2.");
            }

            foreach (WebStreamConfiguration stream in zone.WebStreams ?? [])
            {
                if (string.IsNullOrWhiteSpace(stream.Name))
                    errors.Add($"Zone '{zone.Name}' contains a web stream without a name.");
                else if (!webStreamNames.Add(stream.Name.Trim()))
                    errors.Add($"Web stream name '{stream.Name}' is duplicated.");

                if (!Uri.TryCreate(stream.Url, UriKind.Absolute, out Uri? uri) ||
                    (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                     !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add($"Web stream '{stream.Name}' must use an absolute HTTP or HTTPS URL.");
                }
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
        configuration.NormalizeGroups();

        foreach (SystemConfiguration system in configuration.Systems)
        {
            system.TransportEncryptionMode = string.IsNullOrWhiteSpace(system.TransportEncryptionMode)
                ? "auto"
                : system.TransportEncryptionMode.Trim().ToLowerInvariant();
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

    private static void LoadOptionalAliases(ConsoleConfiguration configuration)
    {
        foreach (SystemConfiguration system in configuration.Systems)
        {
            string aliasPath = ResolvePath(configuration, system.AliasPath);
            if (File.Exists(aliasPath))
                system.RidAlias = AliasFileLoader.Load(aliasPath);
        }
    }
}
