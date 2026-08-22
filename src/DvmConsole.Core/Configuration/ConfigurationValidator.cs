namespace DvmConsole.Core.Configuration;

internal static class ConfigurationValidator
{
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
                else if (channel.Mode == "nxdn" && destinationId > ushort.MaxValue)
                    errors.Add($"NXDN channel '{channel.Name}' must use a 16-bit destination ID.");

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
}
