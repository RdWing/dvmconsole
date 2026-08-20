using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ReceiveStreamLifecycleTests
{
    private static readonly TimeSpan InactivityTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TombstoneLifetime = TimeSpan.FromSeconds(5);

    [Fact]
    public void ExplicitTerminationTombstonesLateVoice()
    {
        var lifecycle = CreateLifecycle();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        Assert.Equal(ReceiveStreamTransition.Started, lifecycle.ObserveVoice(7, now).Transition);
        Assert.Equal(ReceiveStreamTransition.Ended, lifecycle.ObserveTerminator(7, now.AddSeconds(1)).Transition);
        Assert.Equal(ReceiveStreamTransition.IgnoredLate, lifecycle.ObserveVoice(7, now.AddSeconds(2)).Transition);
        Assert.Null(lifecycle.ActiveStreamId);
    }

    [Fact]
    public void DefinitiveStartReopensAReusedTombstonedStreamId()
    {
        var lifecycle = CreateLifecycle();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        lifecycle.ObserveVoice(7, now);
        lifecycle.ObserveTerminator(7, now.AddSeconds(1));

        Assert.Equal(
            ReceiveStreamTransition.IgnoredLate,
            lifecycle.ObserveVoice(7, now.AddSeconds(2)).Transition);
        Assert.Equal(
            ReceiveStreamTransition.Started,
            lifecycle.ObserveDefinitiveStart(7, now.AddSeconds(2.1)).Transition);
        Assert.Equal((uint)7, lifecycle.ActiveStreamId);
    }

    [Fact]
    public void TimeoutGraceResumesTheSameStream()
    {
        var lifecycle = CreateLifecycle();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        lifecycle.ObserveVoice(8, now);
        Assert.Equal(ReceiveStreamTransition.GraceStarted, lifecycle.Advance(now.AddSeconds(2)).Transition);
        Assert.Equal(ReceiveStreamTransition.Resumed, lifecycle.ObserveVoice(8, now.AddSeconds(3.5)).Transition);
        Assert.Equal((uint)8, lifecycle.ActiveStreamId);
    }

    [Fact]
    public void GraceExpiryEndsAndTombstonesTheStream()
    {
        var lifecycle = CreateLifecycle();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        lifecycle.ObserveVoice(9, now);
        lifecycle.Advance(now.AddSeconds(2));
        ReceiveStreamDecision expired = lifecycle.Advance(now.AddSeconds(4));

        Assert.Equal(ReceiveStreamTransition.GraceExpired, expired.Transition);
        Assert.Equal((uint)9, expired.EndedStreamId);
        Assert.Equal(ReceiveStreamTransition.IgnoredLate, lifecycle.ObserveVoice(9, now.AddSeconds(4.5)).Transition);
    }

    [Fact]
    public void ConcurrentStreamsRemainActiveUntilEachOneEnds()
    {
        var lifecycle = CreateLifecycle();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        lifecycle.ObserveVoice(10, now);
        ReceiveStreamDecision decision = lifecycle.ObserveVoice(11, now.AddMilliseconds(100));

        Assert.Equal(ReceiveStreamTransition.Colliding, decision.Transition);
        Assert.Null(decision.EndedStreamId);
        Assert.Equal((uint)10, decision.ActiveStreamId);
        Assert.Equal(ReceiveStreamTransition.Continued, lifecycle.ObserveVoice(10, now.AddSeconds(1)).Transition);
        Assert.Equal(ReceiveStreamTransition.Ended, lifecycle.ObserveTerminator(11, now.AddSeconds(1.1)).Transition);
        Assert.Equal((uint)10, lifecycle.ActiveStreamId);
    }

    [Fact]
    public void TombstoneExpiresAtItsBoundary()
    {
        var lifecycle = CreateLifecycle();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        lifecycle.ObserveVoice(12, now);
        lifecycle.ObserveTerminator(12, now.AddSeconds(1));

        Assert.Equal(ReceiveStreamTransition.Started, lifecycle.ObserveVoice(12, now.AddSeconds(6)).Transition);
    }

    [Fact]
    public void ContinuedVoiceRefreshesTheInactivityDeadline()
    {
        var lifecycle = CreateLifecycle();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        lifecycle.ObserveVoice(13, now);
        Assert.Equal(ReceiveStreamTransition.Continued, lifecycle.ObserveVoice(13, now.AddSeconds(1.5)).Transition);
        Assert.Equal(ReceiveStreamTransition.None, lifecycle.Advance(now.AddSeconds(3)).Transition);
        Assert.Equal(ReceiveStreamTransition.GraceStarted, lifecycle.Advance(now.AddSeconds(3.5)).Transition);
    }

    [Fact]
    public void NewTrafficDoesNotSilentlyDiscardAStreamWhoseTimerTickWasDelayed()
    {
        var lifecycle = CreateLifecycle();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        lifecycle.ObserveVoice(20, now);
        ReceiveStreamDecision collision = lifecycle.ObserveVoice(21, now.AddSeconds(5));

        Assert.Equal(ReceiveStreamTransition.Colliding, collision.Transition);
        Assert.True(lifecycle.IsActive(20));
        Assert.True(lifecycle.IsActive(21));

        ReceiveStreamDecision expired = lifecycle.Advance(now.AddSeconds(5));
        Assert.Equal(ReceiveStreamTransition.GraceExpired, expired.Transition);
        Assert.Equal((uint)20, expired.EndedStreamId);
        Assert.Equal((uint)21, lifecycle.ActiveStreamId);
    }

    private static ReceiveStreamLifecycle CreateLifecycle()
        => new(InactivityTimeout, GracePeriod, TombstoneLifetime);
}
