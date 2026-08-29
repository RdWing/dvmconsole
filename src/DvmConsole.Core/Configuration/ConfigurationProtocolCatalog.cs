namespace DvmConsole.Core.Configuration;

public sealed record ConfigurationProtocolOption(string Value, string DisplayName, bool UsesDmrSlot = false)
{
    public override string ToString() => DisplayName;
}

public static class ConfigurationProtocolCatalog
{
    private static readonly IReadOnlyList<ConfigurationProtocolOption> ChannelOptions =
    [
        new("p25", "P25 Phase 1"),
        new("dmr", "DMR", UsesDmrSlot: true),
        new("nxdn", "NXDN"),
        new("analog", "Analog")
    ];

    private static readonly IReadOnlyList<ConfigurationProtocolOption> KeyOptions =
        ChannelOptions.Where(option => option.Value != "analog").ToArray();

    public static IReadOnlyList<ConfigurationProtocolOption> ForChannels => ChannelOptions;
    public static IReadOnlyList<ConfigurationProtocolOption> ForEncryptionKeys => KeyOptions;

    public static ConfigurationProtocolOption? Find(string? value)
        => ChannelOptions.FirstOrDefault(option =>
            option.Value.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string DisplayName(string? value)
        => Find(value)?.DisplayName ?? (value ?? string.Empty).Trim().ToUpperInvariant();
}
