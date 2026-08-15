namespace DvmConsole.Core.Settings;

public static class ToolbarClockColorPalette
{
    public const string DefaultColorHex = "#3A3A3A";

    public static IReadOnlyList<string> Colors { get; } =
    [
        DefaultColorHex,
        "#0D47A1",
        "#1B5E20",
        "#B26A00",
        "#8E2424",
        "#5E35B1",
        "#00695C",
        "#37474F"
    ];

    public static string Normalize(string? colorHex)
    {
        string normalized = colorHex?.Trim().ToUpperInvariant() ?? string.Empty;
        return Colors.Contains(normalized, StringComparer.OrdinalIgnoreCase)
            ? normalized
            : DefaultColorHex;
    }
}
