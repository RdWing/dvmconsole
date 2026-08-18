using DvmConsole.Core.Configuration;
using DvmConsole.Media;
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
        var first = new ReceiveAudioDiagnostics(10, 1, 2, 0);

        Assert.True(reporter.ShouldPublish(channel, first, now));
        Assert.False(reporter.ShouldPublish(channel, first, now.AddSeconds(1)));
        Assert.True(reporter.ShouldPublish(
            channel,
            first with { DuplicateOrLatePackets = 3 },
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
            new ReceiveAudioDiagnostics(10, 1, 0, 0),
            now));
        Assert.False(reporter.ShouldPublish(
            channel,
            new ReceiveAudioDiagnostics(11, 2, 0, 0),
            now.AddMilliseconds(100)));
        Assert.True(reporter.ShouldPublish(
            channel,
            new ReceiveAudioDiagnostics(20, 2, 0, 0),
            now.AddMilliseconds(500)));
        Assert.False(reporter.ShouldPublish(
            channel,
            new ReceiveAudioDiagnostics(21, 2, 0, 0),
            now.AddSeconds(1)));
    }

    [Fact]
    public void ChannelsHaveIndependentSnapshots()
    {
        var reporter = new ReceiveDiagnosticsReporter(TimeSpan.FromMilliseconds(500));
        ChannelViewModel first = CreateChannel("First");
        ChannelViewModel second = CreateChannel("Second");
        var diagnostics = new ReceiveAudioDiagnostics(10, 0, 1, 0);

        Assert.True(reporter.ShouldPublish(first, diagnostics, DateTimeOffset.UnixEpoch));
        Assert.True(reporter.ShouldPublish(second, diagnostics, DateTimeOffset.UnixEpoch));
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
