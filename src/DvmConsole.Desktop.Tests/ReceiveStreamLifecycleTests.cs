using DvmConsole.Operations;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ReceiveStreamLifecycleTests
{
    private static readonly TimeSpan InactivityTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TerminatorHold = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TombstoneLifetime = TimeSpan.FromSeconds(5);

    [Fact]
    public void MutableLifecycleMatchesImmutableReducerAcrossEdges()
    {
        var lifecycle = CreateLifecycle();
        var policy = new ReceiveStreamPolicy(
            InactivityTimeout,
            GracePeriod,
            TerminatorHold,
            TombstoneLifetime);
        ReceiveStreamState expected = ReceiveStreamState.Empty;
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        expected = Compare(ReceiveStreamReducer.ObserveVoice(expected, 10, now, policy),
            lifecycle.ObserveVoice(10, now));
        expected = Compare(ReceiveStreamReducer.ObserveVoice(expected, 11, now.AddMilliseconds(100), policy),
            lifecycle.ObserveVoice(11, now.AddMilliseconds(100)));
        expected = Compare(ReceiveStreamReducer.ObserveTerminator(expected, 10, now.AddSeconds(1), policy),
            lifecycle.ObserveTerminator(10, now.AddSeconds(1)));
        expected = Compare(ReceiveStreamReducer.ObserveVoice(expected, 10, now.AddSeconds(2), policy),
            lifecycle.ObserveVoice(10, now.AddSeconds(2)));
        expected = Compare(ReceiveStreamReducer.ObserveDefinitiveStart(expected, 12, now.AddSeconds(2.1), policy),
            lifecycle.ObserveDefinitiveStart(12, now.AddSeconds(2.1)));
        expected = Compare(ReceiveStreamReducer.Complete(expected, 11, now.AddSeconds(2.2), policy),
            lifecycle.Complete(11, now.AddSeconds(2.2)));
        expected = Compare(ReceiveStreamReducer.Advance(expected, now.AddSeconds(4.1), policy),
            lifecycle.Advance(now.AddSeconds(4.1)));
        _ = Compare(ReceiveStreamReducer.Advance(expected, now.AddSeconds(6.2), policy),
            lifecycle.Advance(now.AddSeconds(6.2)));

        ReceiveStreamState Compare(
            ReceiveStreamReduction reduction,
            ReceiveStreamDecision actualDecision)
        {
            Assert.Equal(reduction.Decision, actualDecision);
            AssertEquivalent(reduction.State, lifecycle.Snapshot);
            return reduction.State;
        }
    }

    [Fact]
    public void ContinuedVoiceDoesNotAllocateLifecycleState()
    {
        var lifecycle = CreateLifecycle();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        lifecycle.ObserveVoice(7, now);
        _ = MeasureContinuedVoiceAllocations(lifecycle, now, startingTick: 1);

        long allocatedBytes = Enumerable.Range(0, 5)
            .Select(run => MeasureContinuedVoiceAllocations(
                lifecycle,
                now,
                startingTick: 10_001L + (run * 10_000L)))
            .Min();

        Assert.InRange(allocatedBytes, 0, 1_024);
    }

    private static long MeasureContinuedVoiceAllocations(
        ReceiveStreamLifecycle lifecycle,
        DateTimeOffset now,
        long startingTick)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        ReceiveStreamDecision decision = default;
        for (int index = 0; index < 10_000; index++)
            decision = lifecycle.ObserveVoice(7, now.AddTicks(startingTick + index));
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(ReceiveStreamTransition.Continued, decision.Transition);
        return allocatedBytes;
    }

    [Fact]
    public void TerminatorWaitsForQuietAndAcceptsDelayedVoice()
    {
        var lifecycle = CreateLifecycle();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        Assert.Equal(ReceiveStreamTransition.Started, lifecycle.ObserveVoice(7, now).Transition);
        Assert.Equal(
            ReceiveStreamTransition.TerminationPending,
            lifecycle.ObserveTerminator(7, now.AddSeconds(1)).Transition);
        Assert.Equal(ReceiveStreamTransition.Resumed, lifecycle.ObserveVoice(7, now.AddSeconds(2)).Transition);
        Assert.Equal(ReceiveStreamTransition.None, lifecycle.Advance(now.AddSeconds(3.9)).Transition);

        ReceiveStreamDecision expired = lifecycle.Advance(now.AddSeconds(4));
        Assert.Equal(ReceiveStreamTransition.TerminationExpired, expired.Transition);
        Assert.Equal(now.AddSeconds(2), expired.EndedAt);
        Assert.Equal(ReceiveStreamTransition.IgnoredLate, lifecycle.ObserveVoice(7, now.AddSeconds(4.5)).Transition);
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
            ReceiveStreamTransition.Restarted,
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
        Assert.Equal(
            ReceiveStreamTransition.TerminationPending,
            lifecycle.ObserveTerminator(11, now.AddSeconds(1.1)).Transition);
        Assert.Equal(
            ReceiveStreamTransition.TerminationExpired,
            lifecycle.Advance(now.AddSeconds(3.1)).Transition);
        Assert.Equal((uint)10, lifecycle.ActiveStreamId);
    }

    [Fact]
    public void NewStreamStartsWhilePreviousStreamAwaitsTermination()
    {
        var lifecycle = CreateLifecycle();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        lifecycle.ObserveVoice(10, now);
        lifecycle.ObserveTerminator(10, now.AddSeconds(1));

        ReceiveStreamDecision next = lifecycle.ObserveVoice(11, now.AddSeconds(1.1));

        Assert.Equal(ReceiveStreamTransition.Started, next.Transition);
        Assert.Equal((uint)11, lifecycle.ActiveStreamId);
        Assert.True(lifecycle.IsActive(10));
        Assert.True(lifecycle.IsActive(11));
    }

    [Fact]
    public void TombstoneExpiresAtItsBoundary()
    {
        var lifecycle = CreateLifecycle();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        lifecycle.ObserveVoice(12, now);
        lifecycle.Complete(12, now.AddSeconds(1));

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

    [Fact]
    public void BoundsAttackerControlledActiveAndTombstonedStreamIdentities()
    {
        var lifecycle = new ReceiveStreamLifecycle(
            InactivityTimeout,
            GracePeriod,
            TerminatorHold,
            TombstoneLifetime,
            maximumTrackedStreams: 2);
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        lifecycle.ObserveVoice(10, now);
        lifecycle.ObserveVoice(11, now.AddMilliseconds(1));
        lifecycle.ObserveVoice(12, now.AddMilliseconds(2));

        Assert.Equal(2, lifecycle.Snapshot.ActiveStreams.Count);
        Assert.False(lifecycle.IsActive(10));
        Assert.True(lifecycle.IsActive(11));
        Assert.True(lifecycle.IsActive(12));

        lifecycle.Complete(11, now.AddMilliseconds(3));
        lifecycle.Complete(12, now.AddMilliseconds(4));
        lifecycle.ObserveVoice(13, now.AddMilliseconds(5));
        lifecycle.Complete(13, now.AddMilliseconds(6));

        Assert.Equal(2, lifecycle.Snapshot.Tombstones.Count);
        Assert.DoesNotContain((uint)11, lifecycle.Snapshot.Tombstones.Keys);
        Assert.Contains((uint)12, lifecycle.Snapshot.Tombstones.Keys);
        Assert.Contains((uint)13, lifecycle.Snapshot.Tombstones.Keys);
    }

    private static ReceiveStreamLifecycle CreateLifecycle()
        => new(InactivityTimeout, GracePeriod, TerminatorHold, TombstoneLifetime);

    private static void AssertEquivalent(
        ReceiveStreamState expected,
        ReceiveStreamState actual)
    {
        Assert.Equal(expected.PrimaryStreamId, actual.PrimaryStreamId);
        Assert.Equal(expected.NextInsertionOrder, actual.NextInsertionOrder);
        Assert.Equal(expected.ActiveStreamIds.Order(), actual.ActiveStreamIds.Order());
        Assert.Equal(expected.ActiveStreams.OrderBy(pair => pair.Key),
            actual.ActiveStreams.OrderBy(pair => pair.Key));
        Assert.Equal(expected.Tombstones.OrderBy(pair => pair.Key),
            actual.Tombstones.OrderBy(pair => pair.Key));
    }
}
