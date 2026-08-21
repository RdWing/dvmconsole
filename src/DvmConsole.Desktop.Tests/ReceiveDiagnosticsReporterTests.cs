using DvmConsole.Core.Configuration;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ReceiveDiagnosticsReporterTests
{
    [Fact]
    public void RepeatedCumulativeIssueIsNotRepublished()
    {
        var reporter = new ReceiveDiagnosticsReporter(TimeSpan.FromMilliseconds(500));
        ChannelViewModel channel = CreateChannel();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        var first = new ReceiveWarningDiagnostics(1, 2, 0, 0, 0);

        Assert.True(reporter.ShouldPublish(channel, first, now));
        Assert.False(reporter.ShouldPublish(channel, first, now.AddSeconds(1)));
        Assert.True(reporter.ShouldPublish(
            channel,
            first with { RtpLateOrDuplicatePackets = 3 },
            now.AddSeconds(2)));
    }

    [Fact]
    public void ChangeInsideWindowIsPublishedOnNextEligibleFrame()
    {
        var reporter = new ReceiveDiagnosticsReporter(TimeSpan.FromMilliseconds(500));
        ChannelViewModel channel = CreateChannel();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        Assert.True(reporter.ShouldPublish(
            channel,
            new ReceiveWarningDiagnostics(1, 0, 0, 0, 0),
            now));
        Assert.False(reporter.ShouldPublish(
            channel,
            new ReceiveWarningDiagnostics(2, 0, 0, 0, 0),
            now.AddMilliseconds(100)));
        Assert.True(reporter.ShouldPublish(
            channel,
            new ReceiveWarningDiagnostics(2, 0, 0, 0, 0),
            now.AddMilliseconds(500)));
        Assert.False(reporter.ShouldPublish(
            channel,
            new ReceiveWarningDiagnostics(2, 0, 0, 0, 0),
            now.AddSeconds(1)));
    }

    [Fact]
    public void ChannelsHaveIndependentSnapshots()
    {
        var reporter = new ReceiveDiagnosticsReporter(TimeSpan.FromMilliseconds(500));
        ChannelViewModel first = CreateChannel("First");
        ChannelViewModel second = CreateChannel("Second");
        var diagnostics = new ReceiveWarningDiagnostics(0, 1, 0, 0, 0);

        Assert.True(reporter.ShouldPublish(first, diagnostics, DateTimeOffset.UnixEpoch));
        Assert.True(reporter.ShouldPublish(second, diagnostics, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void SeparatesRtpSequenceAndPostCallLateTraffic()
    {
        var diagnostics = new ReceiveWarningDiagnostics(1, 2, 3, 4, 5);

        Assert.Equal(
            "RTP lost 1, RTP late/duplicate 2, receive queue dropped 3, post-call late 4, malformed 5",
            diagnostics.SummaryText);
    }

    private static ChannelViewModel CreateChannel(string name = "Dispatch")
        => new(new ChannelConfiguration
        {
            Name = name,
            System = "System 1",
            Tgid = "99",
            Mode = "analog"
        });
}
