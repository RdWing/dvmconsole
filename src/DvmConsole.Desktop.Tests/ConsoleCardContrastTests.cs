// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Media;
using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ConsoleCardContrastTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OperationalTextMeetsNormalTextContrast(bool dark, bool touch)
    {
        foreach (var activity in Enum.GetValues<ConsoleCardActivity>())
        {
            double foreground = Luminance(((ISolidColorBrush)ConsoleCardPalette.Text(dark, activity, touch)).Color);
            double background = Luminance(((ISolidColorBrush)ConsoleCardPalette.Background(dark, activity, touch)).Color);
            double ratio = (Math.Max(foreground, background) + .05) / (Math.Min(foreground, background) + .05);
            Assert.True(ratio >= 4.5, $"{activity}, dark={dark}, touch={touch}: {ratio:F3}:1");
        }
    }
    private static double Luminance(Color color)
    {
        static double Linear(byte channel) { double value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }
}
