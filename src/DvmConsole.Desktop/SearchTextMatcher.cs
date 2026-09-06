// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Desktop;

internal static class SearchTextMatcher
{
    private static readonly char[] Separators = [' ', '\t', '\r', '\n'];

    public static bool MatchesAllTerms(string? searchText, params string?[] values)
    {
        string[] terms = (searchText ?? string.Empty).Split(
            Separators,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.All(term => values.Any(value =>
            !string.IsNullOrWhiteSpace(value) &&
            value.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }
}
