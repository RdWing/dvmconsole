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
    public void SingleSessionRecoveryExpansionAllocatesOnlyItsResultArray()
    {
        var registry = new ReceiveAudioRouteRegistry();
        ChannelViewModel[] channels = Enumerable.Range(0, 16)
            .Select(index => CreateChannel($"Channel-{index}"))
            .ToArray();
        foreach (ChannelViewModel channel in channels)
            Assert.True(registry.TryAddSessionRoute(channel, "shared-output"));

        for (int index = 0; index < 100; index++)
            _ = registry.ExpandSharedRouteSessions([channels[index & 15]]);

        const int iterations = 1_000;
        int checksum = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < iterations; index++)
            checksum += registry.ExpandSharedRouteSessions([channels[index & 15]]).Length;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(iterations * channels.Length, checksum);
        Assert.True(
            allocated <= iterations * 256,
            $"Expected no per-call query allocations; observed {allocated / (double)iterations:F1} bytes per expansion.");
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
