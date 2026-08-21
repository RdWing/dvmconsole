using DvmConsole.FneClient;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ReceiveJitterBufferEffectivenessTrackerTests
{
    [Fact]
    public void RetainsCompletedCallEvidencePerConnectionUntilReset()
    {
        var tracker = new ReceiveJitterBufferEffectivenessTracker();

        tracker.Observe("Alpha", Timing(reordered: true, deadlineMisses: 0));
        tracker.Observe("Alpha", Timing(reordered: false, deadlineMisses: 2));
        tracker.Observe("Beta", Timing(reordered: true, deadlineMisses: 1));

        Assert.Equal(new ReceiveJitterBufferEffectiveness(1, 2), tracker.GetSnapshot("Alpha"));
        Assert.Equal(new ReceiveJitterBufferEffectiveness(1, 1), tracker.GetSnapshot("Beta"));

        tracker.Reset("Alpha");

        Assert.Equal(default, tracker.GetSnapshot("Alpha"));
        Assert.Equal(new ReceiveJitterBufferEffectiveness(1, 1), tracker.GetSnapshot("Beta"));
    }

    private static ReceiveWorkItemTiming Timing(bool reordered, int deadlineMisses)
        => new(
            new FneTrafficFrame(
                FneTrafficProtocol.Dmr,
                1,
                2,
                100,
                1,
                "GROUP",
                "VOICE",
                "VOICE",
                1,
                10,
                []),
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            JitterBufferReorderedPacket: reordered,
            JitterBufferDeadlineMissedPackets: deadlineMisses);
}
