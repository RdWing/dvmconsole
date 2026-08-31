using DvmConsole.Core.Configuration;

namespace DvmConsole.Configuration.Yaml;

public static class ConfigurationCopyPolicy
{
    // A copied configuration gets a new trust identity. Credentials may be
    // exported explicitly, but they never ride along with Save a Copy or
    // Duplicate operations.
    public static string RemoveTrustScopedWebAuthorization(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ConfigurationDocument document = ConfigurationDocument.Parse(yaml);
        WebStreamConfiguration[] authorized = document.Configuration.Zones
            .SelectMany(zone => zone.WebStreams)
            .Where(stream =>
                !string.IsNullOrWhiteSpace(stream.AuthUsername) ||
                !string.IsNullOrWhiteSpace(stream.AuthPassword))
            .ToArray();
        if (authorized.Length == 0)
            return yaml;
        if (document.IsReadOnly)
        {
            throw new InvalidOperationException(
                "This read-only YAML contains web-stream authorization and cannot be copied safely. Export it explicitly or remove the authorization first.");
        }

        document.RemoveWebStreamAuthorization();
        return document.Serialize();
    }
}
