using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using DvmConsole.Core.Runtime;

namespace DvmConsole.Operations;

public enum ReceiveSignalKind
{
    Metadata,
    Start,
    Voice,
    End
}

public readonly record struct ReceiveObservation(
    ChannelRouteKey RouteKey,
    uint SourceId,
    uint StreamId,
    int Sequence,
    ReceiveSignalKind Kind,
    DateTimeOffset ObservedAt);

/// <summary>
/// Immutable route table published when session topology or receive ownership changes.
/// </summary>
public sealed class ReceiveRouteSnapshot
{
    private readonly FrozenDictionary<ChannelRouteKey, ReadOnlyCollection<ChannelDefinition>> routes;

    private ReceiveRouteSnapshot(
        long version,
        FrozenDictionary<ChannelRouteKey, ReadOnlyCollection<ChannelDefinition>> routes)
    {
        if (version < 0)
            throw new ArgumentOutOfRangeException(nameof(version));
        Version = version;
        this.routes = routes;
    }

    public long Version { get; }

    public IReadOnlyList<ChannelDefinition> Resolve(ChannelRouteKey key)
        => routes.TryGetValue(key, out ReadOnlyCollection<ChannelDefinition>? channels)
            ? channels
            : Array.Empty<ChannelDefinition>();

    public bool Contains(ChannelSessionId sessionId)
    {
        if (!routes.TryGetValue(
                sessionId.RouteKey,
                out ReadOnlyCollection<ChannelDefinition>? channels))
        {
            return false;
        }

        for (int index = 0; index < channels.Count; index++)
        {
            if (channels[index].SessionId == sessionId)
                return true;
        }
        return false;
    }

    public ChannelDefinition? ResolveOwner(
        ChannelRouteKey key,
        ChannelSessionId? preferredOwner = null)
    {
        if (!routes.TryGetValue(key, out ReadOnlyCollection<ChannelDefinition>? channels))
            return null;

        if (preferredOwner is ChannelSessionId preferred)
        {
            for (int index = 0; index < channels.Count; index++)
            {
                if (channels[index].SessionId == preferred)
                    return channels[index];
            }
        }

        return channels.Count == 0 ? null : channels[0];
    }

    public static ReceiveRouteSnapshot Create(
        long version,
        IEnumerable<ChannelDefinition> channels,
        Func<ChannelDefinition, bool>? include = null)
    {
        ArgumentNullException.ThrowIfNull(channels);
        include ??= static _ => true;

        FrozenDictionary<ChannelRouteKey, ReadOnlyCollection<ChannelDefinition>> routes = channels
            .Where(include)
            .GroupBy(channel => channel.RouteKey)
            .ToFrozenDictionary(
                group => group.Key,
                group => Array.AsReadOnly(group
                    .GroupBy(channel => channel.SessionId)
                    .Select(instance => instance.First())
                    .OrderBy(channel => channel.SessionId.InstanceKey, StringComparer.Ordinal)
                    .ToArray()));
        return new ReceiveRouteSnapshot(version, routes);
    }
}

[Flags]
public enum ReceiveAction
{
    None = 0,
    Present = 1 << 0,
    Deliver = 1 << 1
}

public sealed record ChannelReceiveState(ReceiveStreamState StreamLifecycle)
{
    public static ChannelReceiveState Idle { get; } = new(ReceiveStreamState.Empty);

    public uint PrimaryStreamId => StreamLifecycle.PrimaryStreamId ?? 0;
    public ImmutableHashSet<uint> StreamIds => StreamLifecycle.ActiveStreamIds;
}

public readonly record struct ChannelReceiveDecision(
    ChannelReceiveState State,
    ReceiveAction Actions,
    ReceiveStreamDecision StreamDecision);

public readonly record struct ReceiveRouteStatus(
    uint PrimaryStreamId,
    ImmutableHashSet<uint> StreamIds)
{
    public static ReceiveRouteStatus Idle { get; } = new(
        0,
        ImmutableHashSet<uint>.Empty);
}

public readonly record struct ReceiveRouteDecision(
    ChannelDefinition? Owner,
    ReceiveRouteStatus State,
    ReceiveAction Actions,
    ReceiveStreamDecision StreamDecision);

/// <summary>
/// Owns deterministic physical-stream state for one immutable routing snapshot.
/// Logical call episodes remain the responsibility of the established call
/// episode tracker, which is also the authority used by History and TAR.
/// </summary>
public sealed class ReceiveRouteRuntime
{
    private readonly object sync = new();
    private readonly ReceiveRouteSnapshot snapshot;
    private readonly ReceiveStreamPolicy streamPolicy;
    private readonly Dictionary<ChannelRouteKey, RouteState> states = [];

    public ReceiveRouteRuntime(
        ReceiveRouteSnapshot snapshot,
        ReceiveStreamPolicy? streamPolicy = null)
    {
        this.snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        this.streamPolicy = streamPolicy ?? ReceiveStreamPolicy.Default;
    }

    public ReceiveRouteDecision Observe(
        ReceiveObservation observation,
        ChannelSessionId? preferredOwner = null,
        bool assumeStreamActive = false)
    {
        ChannelRouteKey routeKey = observation.RouteKey;
        ChannelDefinition? owner = snapshot.ResolveOwner(routeKey, preferredOwner);
        if (owner is null)
        {
            return new ReceiveRouteDecision(
                null,
                ReceiveRouteStatus.Idle,
                ReceiveAction.None,
                default);
        }

        lock (sync)
        {
            states.TryGetValue(routeKey, out RouteState? current);
            if (current is not null && current.LastObservation == observation)
            {
                return new ReceiveRouteDecision(
                    owner,
                    current.LastObservationReduction.State,
                    current.LastObservationReduction.Actions,
                    current.LastObservationReduction.StreamDecision);
            }

            current ??= AddRouteState(routeKey);
            if (assumeStreamActive &&
                observation.Kind == ReceiveSignalKind.End &&
                !current.IsActive(observation.StreamId))
            {
                current.AssumeActive(observation.StreamId, observation.ObservedAt);
            }

            RouteReduction reduced = current.Observe(observation);
            return new ReceiveRouteDecision(
                owner,
                reduced.State,
                reduced.Actions,
                reduced.StreamDecision);
        }
    }

    public ReceiveRouteDecision Advance(
        ChannelRouteKey routeKey,
        DateTimeOffset now,
        ChannelSessionId? preferredOwner = null)
    {
        ChannelDefinition? owner = snapshot.ResolveOwner(routeKey, preferredOwner);
        if (owner is null)
        {
            return new ReceiveRouteDecision(
                null,
                ReceiveRouteStatus.Idle,
                ReceiveAction.None,
                default);
        }

        lock (sync)
        {
            if (!states.TryGetValue(routeKey, out RouteState? current))
            {
                return new ReceiveRouteDecision(
                    owner,
                    ReceiveRouteStatus.Idle,
                    ReceiveAction.None,
                    default);
            }

            RouteReduction reduced = current.Advance(now);
            return new ReceiveRouteDecision(
                owner,
                reduced.State,
                reduced.Actions,
                reduced.StreamDecision);
        }
    }

    public bool IsActive(ChannelRouteKey routeKey, uint streamId)
    {
        lock (sync)
        {
            return states.TryGetValue(routeKey, out RouteState? current) &&
                   current.IsActive(streamId);
        }
    }

    public bool HasLiveTombstone(
        ChannelRouteKey routeKey,
        uint streamId,
        DateTimeOffset now)
    {
        lock (sync)
        {
            return states.TryGetValue(routeKey, out RouteState? current) &&
                   current.HasLiveTombstone(streamId, now);
        }
    }

    public ChannelReceiveState GetState(ChannelRouteKey routeKey)
    {
        lock (sync)
        {
            return states.TryGetValue(routeKey, out RouteState? current)
                ? new ChannelReceiveState(current.Snapshot)
                : ChannelReceiveState.Idle;
        }
    }

    private RouteState AddRouteState(ChannelRouteKey routeKey)
    {
        var state = new RouteState(streamPolicy);
        states.Add(routeKey, state);
        return state;
    }

    private sealed class RouteState
    {
        private readonly ReceiveStreamStateMachine stateMachine;

        public RouteState(ReceiveStreamPolicy streamPolicy)
            => stateMachine = new ReceiveStreamStateMachine(streamPolicy);

        public ReceiveObservation LastObservation { get; private set; }
        public RouteReduction LastObservationReduction { get; private set; }
        public ReceiveStreamState Snapshot => stateMachine.Snapshot;

        public bool IsActive(uint streamId) => stateMachine.IsActive(streamId);

        public bool HasLiveTombstone(uint streamId, DateTimeOffset now)
            => stateMachine.HasLiveTombstone(streamId, now);

        public void AssumeActive(uint streamId, DateTimeOffset now)
            => stateMachine.AssumeActive(streamId, now);

        public RouteReduction Observe(ReceiveObservation observation)
        {
            bool wasActive = stateMachine.IsActive(observation.StreamId);
            ReceiveStreamDecision decision = observation.Kind switch
            {
                ReceiveSignalKind.End => stateMachine.ObserveTerminator(
                    observation.StreamId,
                    observation.ObservedAt),
                ReceiveSignalKind.Start => stateMachine.ObserveDefinitiveStart(
                    observation.StreamId,
                    observation.ObservedAt),
                ReceiveSignalKind.Voice => stateMachine.ObserveVoice(
                    observation.StreamId,
                    observation.ObservedAt),
                ReceiveSignalKind.Metadata when wasActive => stateMachine.ObserveVoice(
                    observation.StreamId,
                    observation.ObservedAt),
                _ => new ReceiveStreamDecision(
                    ReceiveStreamTransition.None,
                    stateMachine.PrimaryStreamId)
            };
            ReceiveAction actions = ChannelReceiveActionPolicy.ForObservation(
                observation.Kind,
                decision.Transition);
            var reduction = new RouteReduction(CurrentStatus, actions, decision);
            LastObservation = observation;
            LastObservationReduction = reduction;
            return reduction;
        }

        public RouteReduction Advance(DateTimeOffset now)
        {
            ReceiveStreamDecision decision = stateMachine.Advance(now);
            ReceiveAction actions = decision.Transition is
                ReceiveStreamTransition.GraceExpired or
                ReceiveStreamTransition.TerminationExpired
                    ? ReceiveAction.Present
                    : ReceiveAction.None;
            return new RouteReduction(CurrentStatus, actions, decision);
        }

        private ReceiveRouteStatus CurrentStatus => new(
            stateMachine.PrimaryStreamId ?? 0,
            stateMachine.ActiveStreamIds);
    }

    private readonly record struct RouteReduction(
        ReceiveRouteStatus State,
        ReceiveAction Actions,
        ReceiveStreamDecision StreamDecision);
}

internal static class ChannelReceiveActionPolicy
{
    private const ReceiveAction DeliveryActions = ReceiveAction.Present | ReceiveAction.Deliver;

    public static ReceiveAction ForObservation(
        ReceiveSignalKind kind,
        ReceiveStreamTransition transition)
        => transition switch
        {
            ReceiveStreamTransition.None when kind == ReceiveSignalKind.Metadata
                => DeliveryActions,
            ReceiveStreamTransition.None => ReceiveAction.None,
            ReceiveStreamTransition.IgnoredLate => ReceiveAction.Present,
            _ => DeliveryActions
        };
}

/// <summary>
/// Deterministic physical-stream reducer. It owns no clocks, threads, decoders,
/// presentation objects, logical call episodes, or I/O.
/// </summary>
public static class ChannelReceiveReducer
{
    public static ChannelReceiveDecision Reduce(
        ChannelReceiveState state,
        ReceiveObservation observation,
        ReceiveStreamPolicy? streamPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (observation.StreamId == 0)
            return new ChannelReceiveDecision(state, ReceiveAction.None, default);

        streamPolicy ??= ReceiveStreamPolicy.Default;
        bool wasActive = state.StreamLifecycle.IsActive(observation.StreamId);
        ReceiveStreamReduction reduction = observation.Kind switch
        {
            ReceiveSignalKind.End => ReceiveStreamReducer.ObserveTerminator(
                state.StreamLifecycle,
                observation.StreamId,
                observation.ObservedAt,
                streamPolicy),
            ReceiveSignalKind.Start => ReceiveStreamReducer.ObserveDefinitiveStart(
                state.StreamLifecycle,
                observation.StreamId,
                observation.ObservedAt,
                streamPolicy),
            ReceiveSignalKind.Voice => ReceiveStreamReducer.ObserveVoice(
                state.StreamLifecycle,
                observation.StreamId,
                observation.ObservedAt,
                streamPolicy),
            ReceiveSignalKind.Metadata when wasActive => ReceiveStreamReducer.ObserveVoice(
                state.StreamLifecycle,
                observation.StreamId,
                observation.ObservedAt,
                streamPolicy),
            _ => new ReceiveStreamReduction(
                state.StreamLifecycle,
                new ReceiveStreamDecision(
                    ReceiveStreamTransition.None,
                    state.StreamLifecycle.PrimaryStreamId))
        };

        ReceiveAction actions = ChannelReceiveActionPolicy.ForObservation(
            observation.Kind,
            reduction.Decision.Transition);
        return new ChannelReceiveDecision(
            new ChannelReceiveState(reduction.State),
            actions,
            reduction.Decision);
    }

    public static ChannelReceiveDecision Advance(
        ChannelReceiveState state,
        DateTimeOffset now,
        ReceiveStreamPolicy? streamPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ReceiveStreamReduction reduction = ReceiveStreamReducer.Advance(
            state.StreamLifecycle,
            now,
            streamPolicy ?? ReceiveStreamPolicy.Default);
        ReceiveAction actions = reduction.Decision.Transition is
            ReceiveStreamTransition.GraceExpired or
            ReceiveStreamTransition.TerminationExpired
                ? ReceiveAction.Present
                : ReceiveAction.None;
        return new ChannelReceiveDecision(
            new ChannelReceiveState(reduction.State),
            actions,
            reduction.Decision);
    }
}
