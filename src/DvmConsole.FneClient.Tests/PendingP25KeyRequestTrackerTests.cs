using DvmConsole.FneClient;
using Xunit;

namespace DvmConsole.FneClient.Tests;

public sealed class PendingP25KeyRequestTrackerTests
{
    [Fact]
    public void MatchingRequestCanBeConsumedOnlyOnce()
    {
        var tracker = new PendingP25KeyRequestTracker(TimeProvider.System);
        tracker.Register(0x84, 0x50);

        Assert.False(tracker.TryConsume(0x81, 0x50));
        Assert.False(tracker.TryConsume(0x84, 0x51));
        Assert.True(tracker.TryConsume(0x84, 0x50));
        Assert.False(tracker.TryConsume(0x84, 0x50));
    }

    [Fact]
    public void ExpiredRequestIsRejectedAndRemoved()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var tracker = new PendingP25KeyRequestTracker(timeProvider);
        tracker.Register(0x84, 0x50);
        timeProvider.Advance(PendingP25KeyRequestTracker.ResponseWindow + TimeSpan.FromTicks(1));

        Assert.False(tracker.TryConsume(0x84, 0x50));
        Assert.False(tracker.TryConsume(0x84, 0x50));
    }

    [Fact]
    public void FailedSendCannotCancelNewerReplacementRequest()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var tracker = new PendingP25KeyRequestTracker(timeProvider);
        DateTimeOffset firstExpiry = tracker.Register(0x84, 0x50);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        tracker.Register(0x84, 0x50);

        Assert.False(tracker.TryCancel(0x84, 0x50, firstExpiry));
        Assert.True(tracker.TryConsume(0x84, 0x50));
    }

    [Fact]
    public void ClearRemovesAllRequests()
    {
        var tracker = new PendingP25KeyRequestTracker(TimeProvider.System);
        tracker.Register(0x84, 0x50);
        tracker.Register(0x81, 0x51);

        tracker.Clear();

        Assert.False(tracker.TryConsume(0x84, 0x50));
        Assert.False(tracker.TryConsume(0x81, 0x51));
    }

    private sealed class ManualTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = initialUtcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow = utcNow.Add(duration);
    }
}
