using DvmConsole.Core.Diagnostics;

namespace DvmConsole.Desktop;

internal static class DebugLogSearch
{
    public static bool Matches(DebugLogEntry entry, string? searchText)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return SearchTextMatcher.MatchesAllTerms(searchText, entry.Summary);
    }
}
