using DvmConsole.Core.Runtime;
using DvmConsole.Operations;
using Xunit;

namespace DvmConsole.Operations.Tests;

public sealed class OperationsModelTests
{
    [Fact]
    public void ChannelIdentityNormalizesCaseAndNonDmrSlot()
    {
        var first = new ChannelSessionId(" SKYNET ", ChannelProtocol.P25, 3100, 1, " Dispatch ");
        var second = new ChannelSessionId("skynet", ChannelProtocol.P25, 3100, 0, "dispatch");

        Assert.Equal(first, second);
        Assert.Equal((byte)0, first.Slot);
    }

    [Fact]
    public void RouteSnapshotDeduplicatesAnInstanceAndKeepsStableOrder()
    {
        ChannelDefinition beta = CreateDefinition("beta");
        ChannelDefinition alpha = CreateDefinition("alpha");

        ReceiveRouteSnapshot snapshot = ReceiveRouteSnapshot.Create(7, [beta, alpha, alpha]);
        IReadOnlyList<ChannelDefinition> resolved = snapshot.Resolve(alpha.RouteKey);

        Assert.Equal(7, snapshot.Version);
        Assert.Equal(["alpha", "beta"], resolved.Select(channel => channel.SessionId.InstanceKey));
    }

    [Fact]
    public void ReducerTracksEveryPhysicalStreamUntilTerminationExpires()
    {
        DateTimeOffset startedAt = DateTimeOffset.Parse("2026-08-24T00:00:00Z");
        ChannelReceiveDecision first = ChannelReceiveReducer.Reduce(
            ChannelReceiveState.Idle,
            Observe(100, ReceiveSignalKind.Voice, startedAt));
        ChannelReceiveDecision collision = ChannelReceiveReducer.Reduce(
            first.State,
            Observe(200, ReceiveSignalKind.Voice, startedAt.AddMilliseconds(100)));
        ChannelReceiveDecision firstEnd = ChannelReceiveReducer.Reduce(
            collision.State,
            Observe(100, ReceiveSignalKind.End, startedAt.AddMilliseconds(200)));
        ChannelReceiveDecision secondEnd = ChannelReceiveReducer.Reduce(
            firstEnd.State,
            Observe(200, ReceiveSignalKind.End, startedAt.AddMilliseconds(300)));
        ChannelReceiveDecision firstExpired = ChannelReceiveReducer.Advance(
            secondEnd.State,
            startedAt.AddMilliseconds(2300));
        ChannelReceiveDecision secondExpired = ChannelReceiveReducer.Advance(
            firstExpired.State,
            startedAt.AddMilliseconds(2300));

        Assert.Equal(ReceiveStreamTransition.Started, first.StreamDecision.Transition);
        Assert.Equal(ReceiveStreamTransition.Colliding, collision.StreamDecision.Transition);
        Assert.Equal(2, collision.State.StreamIds.Count);
        Assert.Equal(ReceiveStreamTransition.TerminationPending, firstEnd.StreamDecision.Transition);
        Assert.Equal(ReceiveStreamTransition.TerminationPending, secondEnd.StreamDecision.Transition);
        Assert.Equal(ReceiveAction.Present | ReceiveAction.Deliver, secondEnd.Actions);
        Assert.Equal(ReceiveStreamTransition.TerminationExpired, firstExpired.StreamDecision.Transition);
        Assert.Equal(ReceiveStreamTransition.TerminationExpired, secondExpired.StreamDecision.Transition);
        Assert.Equal(ReceiveAction.Present, firstExpired.Actions);
        Assert.Equal(ReceiveAction.Present, secondExpired.Actions);
        Assert.Empty(secondExpired.State.StreamIds);
    }

    [Fact]
    public void LatePacketsArePresentedButNeverDelivered()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-24T00:00:00Z");
        ChannelReceiveState state = ChannelReceiveReducer.Reduce(
            ChannelReceiveState.Idle,
            Observe(7, ReceiveSignalKind.Voice, now)).State;
        state = ChannelReceiveReducer.Advance(state, now.AddSeconds(1)).State;
        state = ChannelReceiveReducer.Advance(state, now.AddSeconds(2)).State;

        ChannelReceiveDecision late = ChannelReceiveReducer.Reduce(
            state,
            Observe(7, ReceiveSignalKind.Voice, now.AddSeconds(2.1)));

        Assert.Equal(ReceiveStreamTransition.IgnoredLate, late.StreamDecision.Transition);
        Assert.Equal(ReceiveAction.Present, late.Actions);
    }

    [Fact]
    public void RuntimeReplaysOneObservationForIndependentConsumers()
    {
        ChannelDefinition alpha = CreateDefinition("alpha");
        ChannelDefinition beta = CreateDefinition("beta");
        var runtime = new ReceiveRouteRuntime(
            ReceiveRouteSnapshot.Create(1, [alpha, beta]));
        ReceiveObservation observation = Observe(
            100,
            ReceiveSignalKind.Voice,
            DateTimeOffset.Parse("2026-08-24T00:00:00Z"));

        ReceiveRouteDecision audio = runtime.Observe(observation, alpha.SessionId);
        ReceiveRouteDecision patch = runtime.Observe(observation, beta.SessionId);

        Assert.Equal(audio.State, patch.State);
        Assert.Equal(audio.Actions, patch.Actions);
        Assert.Equal(alpha, audio.Owner);
        Assert.Equal(beta, patch.Owner);
        Assert.Equal(ReceiveAction.Present | ReceiveAction.Deliver, patch.Actions);
    }

    private static ChannelDefinition CreateDefinition(string instance)
        => new(
            new ChannelSessionId("skynet", ChannelProtocol.P25, 3100, 0, instance),
            instance,
            RxOnly: false,
            IsEncrypted: false);

    private static ReceiveObservation Observe(
        uint streamId,
        ReceiveSignalKind kind,
        DateTimeOffset observedAt)
        => new(
            new ChannelRouteKey("SKYNET", ChannelProtocol.P25, 3100, 0),
            SourceId: 1001,
            streamId,
            Sequence: 1,
            kind,
            observedAt);
}
