using DvmConsole.Core.Diagnostics;

namespace DvmConsole.Desktop;

internal static class DebugLogSearch
{
    private static readonly char[] Separators = [' ', '\t', '\r', '\n'];

    public static bool Matches(DebugLogEntry entry, string? searchText)
    {
        ArgumentNullException.ThrowIfNull(entry);

        string[] terms = (searchText ?? string.Empty).Split(
            Separators,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.All(term => entry.Summary.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
}
