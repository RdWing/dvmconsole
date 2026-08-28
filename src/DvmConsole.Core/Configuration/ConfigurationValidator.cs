namespace DvmConsole.Core.Configuration;

public enum ConfigurationValidationSeverity
{
    Warning,
    Error
}

public sealed record ConfigurationValidationIssue(
    ConfigurationValidationSeverity Severity,
    string Domain,
    string Path,
    string Message)
{
    public bool IsError => Severity == ConfigurationValidationSeverity.Error;
}

public static class ConfigurationValidator
{
    public static IReadOnlyList<string> Validate(ConsoleConfiguration configuration)
        => ValidateDetailed(configuration)
            .Where(issue => issue.IsError)
            .Select(issue => issue.Message)
            .ToArray();

    public static IReadOnlyList<ConfigurationValidationIssue> ValidateDetailed(ConsoleConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var issues = new List<ConfigurationValidationIssue>();
        var systemNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Error(string domain, string path, string message)
            => issues.Add(new ConfigurationValidationIssue(
                ConfigurationValidationSeverity.Error,
                domain,
                path,
                message));

        void Warning(string domain, string path, string message)
            => issues.Add(new ConfigurationValidationIssue(
                ConfigurationValidationSeverity.Warning,
                domain,
                path,
                message));

        if (configuration.Systems.Count == 0)
            Error("Systems", "systems", "At least one system is required.");

        for (int systemIndex = 0; systemIndex < configuration.Systems.Count; systemIndex++)
        {
            SystemConfiguration system = configuration.Systems[systemIndex];
            string path = $"systems[{systemIndex}]";
            if (string.IsNullOrWhiteSpace(system.Name))
                Error("Systems", $"{path}.name", "Every system must have a name.");
            else if (!systemNames.Add(system.Name))
                Error("Systems", $"{path}.name", $"System name '{system.Name}' is duplicated.");

            if (string.IsNullOrWhiteSpace(system.Address))
                Error("Systems", $"{path}.address", $"System '{system.Name}' must have an address.");
            if (system.Port is < 1 or > 65535)
                Error("Systems", $"{path}.port", $"System '{system.Name}' has an invalid port.");
            if (system.Encrypted && string.IsNullOrWhiteSpace(system.PresharedKey))
                Error("Systems", $"{path}.presharedKey", $"System '{system.Name}' must have a preshared key when FNE transport encryption is enabled.");
            if (system.TransportEncryptionMode is not ("auto" or "ecb" or "cbc"))
                Error("Systems", $"{path}.transportEncryptionMode", $"System '{system.Name}' has unsupported transport encryption mode '{system.TransportEncryptionMode}'.");
        }

        var zoneNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var channelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var webStreamNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int zoneIndex = 0; zoneIndex < configuration.Zones.Count; zoneIndex++)
        {
            ZoneConfiguration zone = configuration.Zones[zoneIndex];
            string zonePath = $"zones[{zoneIndex}]";
            if (string.IsNullOrWhiteSpace(zone.Name))
                Error("Zones", $"{zonePath}.name", "Every zone must have a name.");
            else if (!zoneNames.Add(zone.Name.Trim()))
                Error("Zones", $"{zonePath}.name", $"Zone name '{zone.Name}' is duplicated.");

            for (int channelIndex = 0; channelIndex < zone.Channels.Count; channelIndex++)
            {
                ChannelConfiguration channel = zone.Channels[channelIndex];
                string path = $"{zonePath}.channels[{channelIndex}]";
                string channelName = channel.Name?.Trim() ?? string.Empty;
                string channelSystem = channel.System?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(channel.Name))
                    Error("Channels", $"{path}.name", $"Zone '{zone.Name}' contains a channel without a name.");
                else if (!channelNames.Add($"{channelSystem}\u001F{channelName}"))
                    Error("Channels", $"{path}.name", $"Channel name '{channel.Name}' is duplicated in system '{channel.System}'.");

                if (!systemNames.Contains(channelSystem))
                    Error("Channels", $"{path}.system", $"Channel '{channel.Name}' references unknown system '{channel.System}'.");

                if (channel.Mode is not ("dmr" or "p25" or "nxdn" or "analog"))
                    Error("Channels", $"{path}.mode", $"Channel '{channel.Name}' has unsupported mode '{channel.Mode}'.");

                if (!uint.TryParse(channel.Tgid, out uint destinationId) || destinationId == 0)
                    Error("Channels", $"{path}.tgid", $"Channel '{channel.Name}' must have a non-zero numeric destination ID.");
                else if (channel.Mode == "nxdn" && destinationId > ushort.MaxValue)
                    Error("Channels", $"{path}.tgid", $"NXDN channel '{channel.Name}' must use a 16-bit destination ID.");

                if (channel.Mode == "dmr" && channel.Slot is < 1 or > 2)
                    Error("Channels", $"{path}.slot", $"DMR channel '{channel.Name}' must use slot 1 or 2.");

                if (channel.CardSize is not ("small" or "normal" or "large"))
                    Error("Channels", $"{path}.card_size", $"Channel '{channel.Name}' has unsupported card size '{channel.CardSize}'. Use Small, Normal, or Large.");

                if (!string.IsNullOrWhiteSpace(channel.ResourceColor) &&
                    !TryParseColor(channel.ResourceColor))
                {
                    Error("Channels", $"{path}.resourceColor", $"Channel '{channel.Name}' has invalid resource color '{channel.ResourceColor}'.");
                }
            }

            List<WebStreamConfiguration> streams = zone.WebStreams ?? [];
            for (int streamIndex = 0; streamIndex < streams.Count; streamIndex++)
            {
                WebStreamConfiguration stream = streams[streamIndex];
                string path = $"{zonePath}.web_streams[{streamIndex}]";
                if (string.IsNullOrWhiteSpace(stream.Name))
                    Error("Web Streams", $"{path}.name", $"Zone '{zone.Name}' contains a web stream without a name.");
                else if (!webStreamNames.Add(stream.Name.Trim()))
                    Error("Web Streams", $"{path}.name", $"Web stream name '{stream.Name}' is duplicated.");

                if (!Uri.TryCreate(stream.Url, UriKind.Absolute, out Uri? uri) ||
                    (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                     !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
                {
                    Error("Web Streams", $"{path}.url", $"Web stream '{stream.Name}' must use an absolute HTTP or HTTPS URL.");
                }
                else if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
                    Warning("Web Streams", $"{path}.url", $"Web stream '{stream.Name}' uses an unencrypted HTTP URL.");
            }
        }

        var groupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int groupIndex = 0;
        foreach (GroupConfiguration group in configuration.EffectiveGroups())
        {
            string path = $"groups[{groupIndex++}]";
            if (string.IsNullOrWhiteSpace(group.Name))
                Error("Groups", $"{path}.name", "Every group must have a name.");
            else if (!groupNames.Add(group.Name.Trim()))
                Error("Groups", $"{path}.name", $"Group name '{group.Name}' is duplicated.");

            if (!group.IsPatchGroup() && !group.IsMultiselectGroup())
                Error("Groups", $"{path}.type", $"Group '{group.Name}' has unsupported type '{group.Type}'.");
        }

        return issues;
    }

    private static bool TryParseColor(string value)
    {
        string candidate = value.Trim();
        if (candidate.Length is not (4 or 7 or 9) || candidate[0] != '#')
            return false;
        return candidate.Skip(1).All(Uri.IsHexDigit);
    }
}
