using DvmConsole.Core.Configuration;
using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ReceiveAudioRouteRegistryTests
{
    [Fact]
    public void RecoveryExpansionIncludesEverySessionOnSharedPhysicalRoute()
    {
        var registry = new ReceiveAudioRouteRegistry();
        ChannelViewModel first = CreateChannel("First");
        ChannelViewModel second = CreateChannel("Second");
        Assert.True(registry.TryAddSessionRoute(first, "Output-1"));
        Assert.True(registry.TryAddSessionRoute(second, "output-1"));

        ChannelViewModel[] expanded = registry.ExpandSharedRouteSessions([first]);

        Assert.Equal(2, expanded.Length);
        Assert.Contains(first, expanded);
        Assert.Contains(second, expanded);
    }

    [Fact]
    public void DefaultRefreshSelectsOnlyActiveFollowingSessions()
    {
        var registry = new ReceiveAudioRouteRegistry();
        ChannelViewModel following = CreateChannel("Following");
        ChannelViewModel fixedRoute = CreateChannel("Fixed");
        ChannelViewModel inactive = CreateChannel("Inactive");
        registry.AddSessionPolicy(following, followsSystemDefault: true);
        registry.AddSessionPolicy(fixedRoute, followsSystemDefault: false);
        registry.AddSessionPolicy(inactive, followsSystemDefault: true);

        ChannelViewModel[] selected = registry.SelectSystemDefaultSessions(
            channel => ReferenceEquals(channel, following) || ReferenceEquals(channel, fixedRoute));

        Assert.Equal([following], selected);
    }

    private static ChannelViewModel CreateChannel(string name)
        => new(new ChannelConfiguration
        {
            Name = name,
            System = "System",
            Mode = "p25",
            Tgid = "1"
        });
}
