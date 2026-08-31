using DvmConsole.Application;
using DvmConsole.Core.Configuration;
using DvmConsole.FneClient;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ReceiveJitterEventReporterTests
{
    [Fact]
    public void PublishesFirstPeriodicDeltaAndFinalSummary()
    {
        var reporter = new ReceiveJitterEventReporter(TimeSpan.FromSeconds(5));
        ChannelViewModel channel = CreateChannel();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        ReceiveJitterEventPublication first = AssertPublication(
            reporter.Observe(channel, Timing(sequence: 1, missed: 1), now));
        Assert.Equal(ReceiveJitterEventPublicationKind.First, first.Kind);
        Assert.Equal(1, first.MissedSincePrevious);
        Assert.Equal(1, first.TotalMissed);

        Assert.Null(reporter.Observe(
            channel,
            Timing(sequence: 2, missed: 2),
            now.AddSeconds(1)));
        ReceiveJitterEventPublication periodic = AssertPublication(
            reporter.Observe(
                channel,
                Timing(sequence: 3, reordered: true),
                now.AddSeconds(5)));
        Assert.Equal(ReceiveJitterEventPublicationKind.Periodic, periodic.Kind);
        Assert.Equal(1, periodic.ReorderedSincePrevious);
        Assert.Equal(2, periodic.MissedSincePrevious);
        Assert.Equal(3, periodic.TotalMissed);

        Assert.Null(reporter.Observe(
            channel,
            Timing(sequence: 4, missed: 1),
            now.AddSeconds(6)));
        ReceiveJitterEventPublication final = AssertPublication(reporter.Complete(channel, streamId: 77));
        Assert.Equal(ReceiveJitterEventPublicationKind.Final, final.Kind);
        Assert.Equal(1, final.MissedSincePrevious);
        Assert.Equal(4, final.TotalMissed);
        Assert.Null(reporter.Complete(channel, streamId: 77));
    }

    [Fact]
    public void BoundsNeverCompletedStreamState()
    {
        var reporter = new ReceiveJitterEventReporter(
            TimeSpan.FromSeconds(5),
            maximumTrackedStreams: 2);
        ChannelViewModel channel = CreateChannel();

        reporter.Observe(channel, Timing(1, missed: 1, streamId: 1), DateTimeOffset.UnixEpoch);
        reporter.Observe(channel, Timing(1, missed: 1, streamId: 2), DateTimeOffset.UnixEpoch);
        reporter.Observe(channel, Timing(1, missed: 1, streamId: 3), DateTimeOffset.UnixEpoch);

        Assert.Null(reporter.Complete(channel, streamId: 1));
        Assert.NotNull(reporter.Complete(channel, streamId: 2));
        Assert.NotNull(reporter.Complete(channel, streamId: 3));
    }

    private static ReceiveJitterEventPublication AssertPublication(
        ReceiveJitterEventPublication? publication)
        => Assert.IsType<ReceiveJitterEventPublication>(publication);

    private static ChannelViewModel CreateChannel()
        => new(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "100",
            Mode = "p25"
        });

    private static ReceiveWorkItemTiming Timing(
        ushort sequence,
        int missed = 0,
        bool reordered = false,
        uint streamId = 77)
        => new(
            new FneTrafficFrame(
                FneTrafficProtocol.P25,
                1,
                2,
                100,
                null,
                "GROUP",
                "VOICE",
                "LDU1",
                sequence,
                streamId,
                []),
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            JitterBufferReorderedPacket: reordered,
            JitterBufferDeadlineMissedPackets: missed);
}
