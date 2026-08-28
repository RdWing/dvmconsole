using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DvmConsole.Core.Configuration;

public sealed class KeyContainer
{
    public List<KeyEntry> Keys { get; set; } = [];
}

public sealed class KeyEntry
{
    // Existing key files predate multi-protocol support and are P25 unless
    // they explicitly opt into another protocol.
    public string Protocol { get; set; } = "p25";
    public ushort KeyId { get; set; }
    public int AlgId { get; set; }
    public string Key { get; set; } = string.Empty;

    public byte[] KeyBytes => ParseHex(Key);
    public string RequiredLength => KeyFileValidator.DescribeRequiredLength(Protocol, AlgId);

    private static byte[] ParseHex(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        string normalized = value.Trim();
        if (normalized.Length % 2 != 0)
            throw new FormatException("Encryption key material must contain an even number of hexadecimal characters.");

        try
        {
            return Convert.FromHexString(normalized);
        }
        catch (FormatException exception)
        {
            throw new FormatException("Encryption key material contains a non-hexadecimal character.", exception);
        }
    }
}

public static class KeyFileValidator
{
    private const int P25Des = 0x81;
    private const int P25Aes = 0x84;
    private const int P25Arc4 = 0xAA;

    public static IReadOnlyList<ConfigurationValidationIssue> Validate(KeyContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        var issues = new List<ConfigurationValidationIssue>();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < container.Keys.Count; index++)
        {
            KeyEntry key = container.Keys[index];
            string path = $"keys[{index}]";
            string protocol = (key.Protocol ?? string.Empty).Trim().ToLowerInvariant();
            string label = $"Key {index + 1}";

            if (protocol is not ("p25" or "dmr" or "nxdn"))
            {
                issues.Add(Error(path + ".protocol", $"{label} uses unsupported protocol '{key.Protocol}'."));
                continue;
            }

            int maximumKeyId = protocol switch
            {
                "dmr" => byte.MaxValue,
                "nxdn" => 63,
                _ => ushort.MaxValue
            };
            if (key.KeyId is 0 || key.KeyId > maximumKeyId)
            {
                issues.Add(Error(
                    path + ".keyId",
                    $"{label} must use a {protocol.ToUpperInvariant()} key ID between 1 and {maximumKeyId}."));
            }

            if (!TryGetLengthRule(protocol, key.AlgId, out int minimumBytes, out int maximumBytes, out string algorithmName))
            {
                issues.Add(Error(
                    path + ".algId",
                    $"{label} uses unsupported {protocol.ToUpperInvariant()} algorithm ID 0x{key.AlgId:X2}."));
                continue;
            }

            string identity = $"{protocol}\u001F{key.AlgId}\u001F{key.KeyId}";
            if (!identities.Add(identity))
            {
                issues.Add(Error(
                    path,
                    $"{label} duplicates the {protocol.ToUpperInvariant()} algorithm 0x{key.AlgId:X2}, key ID {key.KeyId} entry."));
            }

            byte[] material;
            try
            {
                material = key.KeyBytes;
            }
            catch (FormatException exception)
            {
                issues.Add(Error(path + ".key", $"{label}: {exception.Message}"));
                continue;
            }

            if (material.Length < minimumBytes || material.Length > maximumBytes)
            {
                string required = minimumBytes == maximumBytes
                    ? $"exactly {minimumBytes} bytes"
                    : $"{minimumBytes} to {maximumBytes} bytes";
                issues.Add(Error(
                    path + ".key",
                    $"{label} ({algorithmName}) requires {required} of key material; the current value contains {material.Length} bytes."));
                continue;
            }

            if (protocol == "nxdn" && key.AlgId == 0x01)
            {
                ushort seed = (ushort)((material[0] << 8) | material[1]);
                if ((seed & 0x7FFF) == 0)
                    issues.Add(Error(path + ".key", $"{label} (NXDN EHR) requires a non-zero 15-bit seed."));
            }
        }

        return issues;
    }

    public static string DescribeRequiredLength(string? protocol, int algorithmId)
    {
        if (!TryGetLengthRule((protocol ?? string.Empty).Trim().ToLowerInvariant(), algorithmId,
                out int minimumBytes, out int maximumBytes, out _))
        {
            return "Unsupported algorithm";
        }

        return minimumBytes == maximumBytes
            ? $"{minimumBytes} bytes"
            : $"{minimumBytes}–{maximumBytes} bytes";
    }

    private static bool TryGetLengthRule(
        string protocol,
        int algorithmId,
        out int minimumBytes,
        out int maximumBytes,
        out string algorithmName)
    {
        (minimumBytes, maximumBytes, algorithmName) = (protocol, algorithmId) switch
        {
            ("p25", P25Aes) => (1, 32, "P25 AES"),
            ("p25", P25Des) => (8, 8, "P25 DES-OFB"),
            ("p25", P25Arc4) => (5, 5, "P25 ARC4/ADP"),
            ("dmr", 0x01) => (5, 5, "DMR ARC4"),
            ("dmr", 0x02) => (8, 8, "DMR DES-OFB"),
            ("dmr", 0x05) => (32, 32, "DMR AES-256"),
            ("nxdn", 0x01) => (2, 2, "NXDN EHR"),
            ("nxdn", 0x02) => (8, 8, "NXDN DES"),
            ("nxdn", 0x03) => (32, 32, "NXDN AES-256"),
            _ => (0, 0, string.Empty)
        };
        return minimumBytes != 0;
    }

    private static ConfigurationValidationIssue Error(string path, string message)
        => new(ConfigurationValidationSeverity.Error, "Encryption Keys", path, message);
}

public static class KeyFileLoader
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
        .Build();

    public static KeyContainer Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Key file not found.", fullPath);

        return Parse(File.ReadAllText(fullPath));
    }

    public static KeyContainer Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        KeyContainer container = deserializer.Deserialize<KeyContainer>(yaml)
            ?? throw new InvalidDataException("The key file did not contain a key container.");
        container.Keys ??= [];
        return container;
    }

    public static string Serialize(KeyContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        container.Keys ??= [];
        return Serializer.Serialize(container);
    }
}
