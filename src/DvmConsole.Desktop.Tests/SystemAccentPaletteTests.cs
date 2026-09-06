// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Media;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class SystemAccentPaletteTests
{
    [Fact]
    public void AdjacentSystemsUseStableDistinctAccents()
    {
        Color first = Assert.IsType<SolidColorBrush>(SystemAccentPalette.GetBrush(0)).Color;
        Color second = Assert.IsType<SolidColorBrush>(SystemAccentPalette.GetBrush(1)).Color;

        Assert.NotEqual(first, second);
        Assert.Equal(first, Assert.IsType<SolidColorBrush>(SystemAccentPalette.GetBrush(0)).Color);
    }
}
