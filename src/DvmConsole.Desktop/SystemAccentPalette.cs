using Avalonia.Media;

namespace DvmConsole.Desktop;

internal static class SystemAccentPalette
{
    private static readonly Color[] Colors =
    [
        Color.Parse("#38BDF8"),
        Color.Parse("#F97316"),
        Color.Parse("#A78BFA"),
        Color.Parse("#22C55E"),
        Color.Parse("#F43F5E"),
        Color.Parse("#EAB308"),
        Color.Parse("#14B8A6"),
        Color.Parse("#EC4899")
    ];

    public static IBrush GetBrush(int systemIndex)
    {
        if (systemIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(systemIndex));

        return new SolidColorBrush(Colors[systemIndex % Colors.Length]);
    }
}
