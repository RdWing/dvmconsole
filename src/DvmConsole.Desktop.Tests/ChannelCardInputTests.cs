using Avalonia.Controls;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ChannelCardInputTests
{
    [Fact]
    public void NestedButtonContentIsInteractive()
    {
        var label = new TextBlock { Text = "TAR" };
        var button = new Button { Content = label };
        var card = new Border { Child = button };

        Assert.True(ChannelCardInput.IsInteractiveSource(label, card));
        Assert.True(ChannelCardInput.IsInteractiveSource(button, card));
    }

    [Fact]
    public void SliderSurfaceIsInteractive()
    {
        var slider = new Slider();
        var card = new Border { Child = slider };

        Assert.True(ChannelCardInput.IsInteractiveSource(slider, card));
    }

    [Fact]
    public void DisabledPttAreaRemainsInteractiveForTheContainingCard()
    {
        var button = new Button { IsEnabled = false };
        var guard = new Border { Child = button };
        guard.Classes.Add("ptt-input-guard");
        var card = new Border { Child = guard };

        Assert.True(ChannelCardInput.IsInteractiveSource(guard, card));
    }

    [Fact]
    public void PlainCardContentIsNotInteractive()
    {
        var label = new TextBlock { Text = "Dispatch" };
        var card = new Border { Child = label };

        Assert.False(ChannelCardInput.IsInteractiveSource(label, card));
    }
}
