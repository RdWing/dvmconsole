using Avalonia;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ConfigurationStudioInitialPlacementTests
{
    [Fact]
    public void FitsDefaultStudioInsideA1080pWorkingArea()
    {
        ConfigurationStudioInitialPlacement placement =
            ConfigurationStudioInitialPlacement.FitToWorkingArea(
                new Size(1488, 1058),
                new PixelRect(0, 0, 1920, 1040),
                displayScaling: 1);

        Assert.Equal(new Size(1488, 992), placement.Size);
        Assert.Equal(new PixelPoint(216, 24), placement.Position);
    }

    [Fact]
    public void AccountsForWindowsDisplayScaling()
    {
        ConfigurationStudioInitialPlacement placement =
            ConfigurationStudioInitialPlacement.FitToWorkingArea(
                new Size(1488, 1058),
                new PixelRect(0, 0, 1920, 1040),
                displayScaling: 1.25);

        Assert.Equal(new Size(1488, 784), placement.Size);
        Assert.Equal(new PixelPoint(30, 30), placement.Position);
    }

    [Fact]
    public void LeavesDefaultSizeUnchangedOnALargerDisplay()
    {
        ConfigurationStudioInitialPlacement placement =
            ConfigurationStudioInitialPlacement.FitToWorkingArea(
                new Size(1488, 1058),
                new PixelRect(0, 0, 2560, 1440),
                displayScaling: 1);

        Assert.Equal(new Size(1488, 1058), placement.Size);
        Assert.Equal(new PixelPoint(536, 191), placement.Position);
    }
}
