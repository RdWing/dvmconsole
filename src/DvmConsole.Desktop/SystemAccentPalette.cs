// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Media;

namespace DvmConsole.Desktop;

internal static class SystemAccentPalette
{
    public static IBrush GetBrush(int systemIndex)
    {
        if (systemIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(systemIndex));

        return SolidBrushCache.Get(DvmConsole.Presentation.ConsoleSystemColors.At(systemIndex));
    }
}
