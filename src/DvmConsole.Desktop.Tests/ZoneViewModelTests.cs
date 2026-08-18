using DvmConsole.Core.Configuration;
using DvmConsole.FneClient;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ZoneViewModelTests
{
    [Fact]
    public void ReceiveActivityTracksChannelRuntimeState()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "100",
            Mode = "p25"
        });
        var zone = new ZoneViewModel("Dispatch", [channel], []);

        Assert.False(zone.IsReceiving);
        Assert.True(channel.TryApplyTraffic("System 1", Traffic("VOICE", "LDU1")));
        Assert.True(zone.IsReceiving);
        Assert.Equal(1.0, zone.ActivityBarOpacity);

        Assert.True(channel.TryApplyTraffic("System 1", Traffic("TERMINATOR", "TDU")));
        Assert.False(zone.IsReceiving);
        Assert.True(zone.ActivityBarOpacity < 1.0);
    }

    private static FneTrafficFrame Traffic(string callType, string frameType)
        => new(
            FneTrafficProtocol.P25,
            1,
            42,
            100,
            null,
            "GROUP",
            callType,
            frameType,
            1,
            7,
            []);
}
