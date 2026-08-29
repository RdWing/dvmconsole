using System.Globalization;

namespace DvmConsole.Core.Configuration;

public sealed record EncryptionAlgorithmOption(
    string Protocol,
    string ConfigurationValue,
    string DisplayName,
    int? AlgorithmId,
    int MinimumKeyBytes,
    int MaximumKeyBytes,
    bool RequiresNonZero15BitSeed = false)
{
    public string AlgorithmIdText => AlgorithmId is int value ? $"0x{value:X2}" : "Clear";
    public string ValidationName => $"{ConfigurationProtocolCatalog.DisplayName(Protocol)} {DisplayName}";
    public string RequiredLength => MinimumKeyBytes == MaximumKeyBytes
        ? $"{MinimumKeyBytes} bytes"
        : $"{MinimumKeyBytes}–{MaximumKeyBytes} bytes";

    public override string ToString() => DisplayName;
}

public static class EncryptionAlgorithmCatalog
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<EncryptionAlgorithmOption>> ProtocolOptions =
        new Dictionary<string, IReadOnlyList<EncryptionAlgorithmOption>>(StringComparer.OrdinalIgnoreCase)
        {
            ["p25"] =
            [
                new("p25", "aes", "AES-256", 0x84, 1, 32),
                new("p25", "des", "DES-OFB", 0x81, 8, 8),
                new("p25", "arc4", "RC4 / ADP", 0xAA, 5, 5)
            ],
            ["dmr"] =
            [
                new("dmr", "aes", "AES-256", 0x05, 32, 32),
                new("dmr", "des", "DES-OFB", 0x02, 8, 8),
                new("dmr", "arc4", "RC4", 0x01, 5, 5)
            ],
            ["nxdn"] =
            [
                new("nxdn", "aes", "AES-256", 0x03, 32, 32),
                new("nxdn", "des", "DES", 0x02, 8, 8),
                new("nxdn", "ehr", "EHR", 0x01, 2, 2, RequiresNonZero15BitSeed: true)
            ]
        };

    public static IReadOnlyList<EncryptionAlgorithmOption> ForChannelMode(string? mode)
    {
        string protocol = NormalizeProtocol(mode);
        var clear = new EncryptionAlgorithmOption(protocol, "none", "None (clear)", null, 0, 0);
        return ProtocolOptions.TryGetValue(protocol, out IReadOnlyList<EncryptionAlgorithmOption>? options)
            ? [clear, .. options]
            : [clear];
    }

    public static IReadOnlyList<EncryptionAlgorithmOption> ForKeyProtocol(string? protocol)
        => ProtocolOptions.TryGetValue(NormalizeProtocol(protocol), out IReadOnlyList<EncryptionAlgorithmOption>? options)
            ? options
            : [];

    public static EncryptionAlgorithmOption? FindChannelOption(string? mode, string? configuredValue)
    {
        IReadOnlyList<EncryptionAlgorithmOption> options = ForChannelMode(mode);
        string normalized = (configuredValue ?? "none").Trim();
        if (normalized.Length == 0 || normalized.Equals("clear", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("unencrypted", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "none";
        }

        EncryptionAlgorithmOption? direct = options.FirstOrDefault(option =>
            option.ConfigurationValue.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (direct is not null)
            return direct;

        direct = options.FirstOrDefault(option => option.DisplayName.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (direct is not null)
            return direct;

        string alias = normalized.ToLowerInvariant() switch
        {
            "aes-256" => "aes",
            "des-ofb" => "des",
            "adp" => "arc4",
            _ => normalized
        };
        direct = options.FirstOrDefault(option =>
            option.ConfigurationValue.Equals(alias, StringComparison.OrdinalIgnoreCase));
        if (direct is not null)
            return direct;

        return TryParseAlgorithmId(normalized, out int algorithmId)
            ? options.FirstOrDefault(option => option.AlgorithmId == algorithmId)
            : null;
    }

    public static EncryptionAlgorithmOption? FindKeyOption(string? protocol, int algorithmId)
        => ForKeyProtocol(protocol).FirstOrDefault(option => option.AlgorithmId == algorithmId);

    public static bool TryParseChannelKeyId(string? mode, string? value, out ushort keyId)
    {
        keyId = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string normalized = value.Trim();
        bool hexadecimal = normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                           NormalizeProtocol(mode) == "p25";
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[2..];

        return ushort.TryParse(
                   normalized,
                   hexadecimal ? NumberStyles.AllowHexSpecifier : NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out keyId) &&
               keyId != 0;
    }

    public static string FormatChannelKeyIdDigits(string? mode, string? value)
        => TryParseChannelKeyId(mode, value, out ushort keyId)
            ? keyId.ToString("X", CultureInfo.InvariantCulture)
            : StripHexPrefix(value);

    public static string StripHexPrefix(string? value)
    {
        string normalized = (value ?? string.Empty).Trim();
        return normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? normalized[2..]
            : normalized;
    }

    private static string NormalizeProtocol(string? value)
        => (value ?? string.Empty).Trim().ToLowerInvariant();

    private static bool TryParseAlgorithmId(string value, out int algorithmId)
    {
        string normalized = value.Trim();
        bool hexadecimal = normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (hexadecimal)
            normalized = normalized[2..];
        return int.TryParse(
            normalized,
            hexadecimal ? NumberStyles.AllowHexSpecifier : NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out algorithmId);
    }
}
