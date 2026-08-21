using Avalonia;
using DvmConsole.Core.Settings;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class MainWindowPlacementControllerTests
{
    [Fact]
    public void AcceptsPlacementWithReachableTitleBar()
    {
        var placement = new WindowPlacementSetting
        {
            Left = -100,
            Top = 20,
            Width = 1260,
            Height = 760
        };

        bool visible = MainWindowPlacementController.HasUsableTitleBarIntersection(
            placement,
            new PixelRect(0, 0, 1920, 1080),
            displayScaling: 1);

        Assert.True(visible);
    }

    [Fact]
    public void RejectsPlacementWhoseTitleBarIsOffScreen()
    {
        var placement = new WindowPlacementSetting
        {
            Left = 2200,
            Top = 20,
            Width = 1260,
            Height = 760
        };

        bool visible = MainWindowPlacementController.HasUsableTitleBarIntersection(
            placement,
            new PixelRect(0, 0, 1920, 1080),
            displayScaling: 1);

        Assert.False(visible);
    }

    [Fact]
    public void AccountsForDisplayScalingWhenCheckingVisibility()
    {
        var placement = new WindowPlacementSetting
        {
            Left = -2400,
            Top = 50,
            Width = 1260,
            Height = 760
        };

        bool visible = MainWindowPlacementController.HasUsableTitleBarIntersection(
            placement,
            new PixelRect(-2560, 0, 2560, 1440),
            displayScaling: 2);

        Assert.True(visible);
    }

    [Theory]
    [InlineData(null, 20d)]
    [InlineData(20d, null)]
    public void RejectsIncompleteCoordinates(double? left, double? top)
    {
        var placement = new WindowPlacementSetting
        {
            Left = left,
            Top = top,
            Width = 1260,
            Height = 760
        };

        Assert.False(MainWindowPlacementController.HasUsableTitleBarIntersection(
            placement,
            new PixelRect(0, 0, 1920, 1080),
            displayScaling: 1));
    }
}
