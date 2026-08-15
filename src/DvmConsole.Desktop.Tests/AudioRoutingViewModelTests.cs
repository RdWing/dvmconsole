using DvmConsole.Core.Configuration;
using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class AudioRoutingViewModelTests
{
    [Fact]
    public void ChannelOutputRouteUsesSystemDefaultUntilAnOverrideIsSelected()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "101",
            Mode = "analog"
        });
        var systemDefault = new AudioDeviceOptionViewModel("default", "System default", true);
        var headset = new AudioDeviceOptionViewModel("headset-1", "Headset", false);

        channel.SetOutputDeviceOptions([systemDefault, headset]);

        Assert.Same(systemDefault, channel.SelectedOutputDevice);
        channel.SelectedOutputDevice = headset;
        Assert.Equal("headset-1", channel.OutputDeviceIdText);
        Assert.Same(headset, channel.SelectedOutputDevice);

        channel.SelectedOutputDevice = systemDefault;
        Assert.Equal("default", channel.OutputDeviceIdText);
    }

    [Fact]
    public void WebStreamOutputRouteFallsBackToSystemDefault()
    {
        var stream = new WebStreamViewModel(new WebStreamConfiguration
        {
            Name = "Dispatch stream",
            Url = "https://example.test/stream"
        });
        var systemDefault = new AudioDeviceOptionViewModel("default", "System default", true);
        var speaker = new AudioDeviceOptionViewModel("speaker-1", "Speaker", false);

        stream.SetOutputDeviceOptions([systemDefault, speaker]);
        Assert.Same(systemDefault, stream.SelectedOutputDevice);

        stream.SelectedOutputDevice = speaker;
        Assert.Equal("speaker-1", stream.OutputDeviceIdText);
    }
}
