using DvmConsole.Core.Settings;
using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ToolbarClockViewModelTests
{
    [Fact]
    public void FormatsEnabledClockAtConfiguredUtcOffset()
    {
        var clock = new ToolbarClockViewModel(1, new ToolbarClockSetting
        {
            Enabled = true,
            UtcOffsetHours = 5
        });

        clock.Update(new DateTimeOffset(2026, 8, 15, 12, 5, 9, TimeSpan.Zero), true, true);

        Assert.True(clock.Enabled);
        Assert.Equal("UTC+05", clock.TimeZoneLabel);
        Assert.Equal(
            MainWindowViewModel.FormatClock(new DateTime(2026, 8, 15, 17, 5, 9), true, true),
            clock.TimeText);
    }

    [Fact]
    public void RejectsOffsetsOutsideSupportedToolbarRange()
    {
        var clock = new ToolbarClockViewModel(1, new ToolbarClockSetting());
        clock.UtcOffsetText = "15";

        Assert.False(clock.TryGetUtcOffset(out _));
        Assert.Equal("UTC?", clock.TimeZoneLabel);
    }

    [Fact]
    public void PersistsAndNormalizesToolbarClockColors()
    {
        var clock = new ToolbarClockViewModel(1, new ToolbarClockSetting
        {
            Enabled = true,
            ColorHex = "#0D47A1"
        });

        Assert.Equal("#0D47A1", clock.ColorHex);
        Assert.Equal("Blue", clock.ColorLabel);
        Assert.Equal("#0D47A1", clock.ToSetting().ColorHex);

        clock.ColorHex = "not-a-palette-color";
        Assert.Equal(ToolbarClockColorPalette.DefaultColorHex, clock.ColorHex);
        Assert.Equal("Neutral", clock.ColorLabel);
    }
}
