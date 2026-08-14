using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DvmConsole.Core.Configuration;

public sealed class KeyContainer
{
    public List<KeyEntry> Keys { get; set; } = [];
}

public sealed class KeyEntry
{
    public ushort KeyId { get; set; }
    public int AlgId { get; set; }
    public string Key { get; set; } = string.Empty;

    public byte[] KeyBytes => ParseHex(Key);

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

public static class KeyFileLoader
{
    public static KeyContainer Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Key file not found.", fullPath);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        KeyContainer container = deserializer.Deserialize<KeyContainer>(File.ReadAllText(fullPath))
            ?? throw new InvalidDataException("The key file did not contain a key container.");
        container.Keys ??= [];
        return container;
    }
}
