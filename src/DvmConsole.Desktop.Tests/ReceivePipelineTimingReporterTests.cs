using DvmConsole.Core.Configuration;
using DvmConsole.Desktop;
using DvmConsole.FneClient;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ReceivePipelineTimingReporterTests
{
    [Fact]
    public void PublishesMaterialDelayAtMostOncePerInterval()
    {
        var reporter = new ReceivePipelineTimingReporter(TimeSpan.FromSeconds(5));
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "100",
            Mode = "p25"
        });
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ReceiveWorkItemTiming delayed = Timing(TimeSpan.FromMilliseconds(250));

        Assert.True(reporter.ShouldPublish(channel, delayed, now));
        Assert.False(reporter.ShouldPublish(channel, delayed, now.AddSeconds(1)));
        Assert.True(reporter.ShouldPublish(channel, delayed, now.AddSeconds(5)));
    }

    [Fact]
    public void IgnoresHealthyPipelineTiming()
    {
        var reporter = new ReceivePipelineTimingReporter(TimeSpan.FromSeconds(5));
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "100",
            Mode = "p25"
        });

        Assert.False(reporter.ShouldPublish(
            channel,
            Timing(TimeSpan.FromMilliseconds(25)),
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void PublishesAnFneInterArrivalGapEvenWhenLocalStagesAreFast()
    {
        var reporter = new ReceivePipelineTimingReporter(TimeSpan.FromSeconds(5));
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "100",
            Mode = "p25"
        });

        Assert.True(reporter.ShouldPublish(
            channel,
            Timing(
                TimeSpan.FromMilliseconds(5),
                interArrival: TimeSpan.FromMilliseconds(750)),
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void PublishesATransportInterArrivalGapEvenWhenLaterStagesAreFast()
    {
        var reporter = new ReceivePipelineTimingReporter(TimeSpan.FromSeconds(5));
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "100",
            Mode = "p25"
        });
        ReceiveWorkItemTiming timing = Timing(TimeSpan.FromMilliseconds(5)) with
        {
            TransportInterArrivalDelay = TimeSpan.FromMilliseconds(750)
        };

        Assert.True(reporter.ShouldPublish(channel, timing, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void DoesNotWarnForTheConfiguredJitterBufferDelay()
    {
        var reporter = new ReceivePipelineTimingReporter(TimeSpan.FromSeconds(5));
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "100",
            Mode = "p25"
        });
        ReceiveWorkItemTiming timing = Timing(TimeSpan.FromMilliseconds(190)) with
        {
            ConfiguredJitterBufferDelay = TimeSpan.FromMilliseconds(180)
        };

        Assert.False(reporter.ShouldPublish(channel, timing, DateTimeOffset.UtcNow));
    }

    private static ReceiveWorkItemTiming Timing(
        TimeSpan total,
        TimeSpan? interArrival = null)
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
                1,
                77,
                []),
            interArrival ?? TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(1),
            total,
            TimeSpan.FromMilliseconds(1),
            total);
}
