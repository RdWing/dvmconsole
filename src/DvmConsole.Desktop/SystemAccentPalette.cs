// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Media;

namespace DvmConsole.Desktop;

internal static class SystemAccentPalette
{
    private static readonly string[] Colors =
    [
        "#38BDF8",
        "#F97316",
        "#A78BFA",
        "#22C55E",
        "#F43F5E",
        "#EAB308",
        "#14B8A6",
        "#EC4899"
    ];

    public static IBrush GetBrush(int systemIndex)
    {
        if (systemIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(systemIndex));

        return SolidBrushCache.Get(Colors[systemIndex % Colors.Length]);
    }
}
