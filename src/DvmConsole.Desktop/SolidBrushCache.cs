// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Concurrent;
using Avalonia.Media;

namespace DvmConsole.Desktop;

internal static class SolidBrushCache
{
    private static readonly ConcurrentDictionary<string, IBrush> Brushes =
        new(StringComparer.OrdinalIgnoreCase);

    public static IBrush Get(string color, string? fallback = null)
    {
        string normalized = Normalize(color, fallback);
        return Brushes.GetOrAdd(
            normalized,
            static value => new SolidColorBrush(Color.Parse(value)));
    }

    private static string Normalize(string? color, string? fallback)
    {
        string candidate = string.IsNullOrWhiteSpace(color)
            ? fallback ?? throw new ArgumentException("A color or fallback is required.", nameof(color))
            : color.Trim();
        try
        {
            _ = Color.Parse(candidate);
            return candidate;
        }
        catch (FormatException) when (!string.IsNullOrWhiteSpace(fallback))
        {
            return fallback;
        }
    }
}
