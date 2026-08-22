namespace DvmConsole.Core.Configuration;

internal static class ConfigurationNormalizer
{
    public static void Normalize(ConsoleConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

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
}
