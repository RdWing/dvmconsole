// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Diagnostics;

namespace DvmConsole.Desktop;

internal static class DebugLogSearch
{
    public static bool Matches(DebugLogEntry entry, string? searchText)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(searchText))
            return true;

        return SearchTextMatcher.MatchesAllTerms(searchText, entry.Summary);
    }
}
