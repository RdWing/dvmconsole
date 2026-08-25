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

public readonly record struct ReceiveRouteDecision(
    ChannelDefinition? Owner,
    ChannelReceiveState State,
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
                ChannelReceiveState.Idle,
                ReceiveAction.None,
                default);
        }

        lock (sync)
        {
            states.TryGetValue(routeKey, out RouteState? current);
            if (current?.LastObservation == observation)
            {
                return new ReceiveRouteDecision(
                    owner,
                    current.LastObservationDecision.State,
                    current.LastObservationDecision.Actions,
                    current.LastObservationDecision.StreamDecision);
            }

            ChannelReceiveState state = current?.State ?? ChannelReceiveState.Idle;
            if (assumeStreamActive &&
                observation.Kind == ReceiveSignalKind.End &&
                !state.StreamLifecycle.IsActive(observation.StreamId))
            {
                state = new ChannelReceiveState(ReceiveStreamReducer.AssumeActive(
                    state.StreamLifecycle,
                    observation.StreamId,
                    observation.ObservedAt));
            }

            ChannelReceiveDecision reduced = ChannelReceiveReducer.Reduce(
                state,
                observation,
                streamPolicy);
            if (current is null)
                states[routeKey] = new RouteState(observation, reduced);
            else
                current.Observe(observation, reduced);
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
                ChannelReceiveState.Idle,
                ReceiveAction.None,
                default);
        }

        lock (sync)
        {
            if (!states.TryGetValue(routeKey, out RouteState? current))
            {
                return new ReceiveRouteDecision(
                    owner,
                    ChannelReceiveState.Idle,
                    ReceiveAction.None,
                    default);
            }

            ChannelReceiveDecision reduced = ChannelReceiveReducer.Advance(
                current.State,
                now,
                streamPolicy);
            current.Advance(reduced.State);
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
                   current.State.StreamLifecycle.IsActive(streamId);
        }
    }

    public ChannelReceiveState GetState(ChannelRouteKey routeKey)
    {
        lock (sync)
        {
            return states.TryGetValue(routeKey, out RouteState? current)
                ? current.State
                : ChannelReceiveState.Idle;
        }
    }

    private sealed class RouteState
    {
        public RouteState(
            ReceiveObservation lastObservation,
            ChannelReceiveDecision decision)
        {
            LastObservation = lastObservation;
            LastObservationDecision = decision;
            State = decision.State;
        }

        public ReceiveObservation LastObservation { get; private set; }
        public ChannelReceiveDecision LastObservationDecision { get; private set; }
        public ChannelReceiveState State { get; private set; }

        public void Observe(
            ReceiveObservation observation,
            ChannelReceiveDecision decision)
        {
            LastObservation = observation;
            LastObservationDecision = decision;
            State = decision.State;
        }

        public void Advance(ChannelReceiveState state) => State = state;
    }
}

/// <summary>
/// Deterministic physical-stream reducer. It owns no clocks, threads, decoders,
/// presentation objects, logical call episodes, or I/O.
/// </summary>
public static class ChannelReceiveReducer
{
    private const ReceiveAction DeliveryActions = ReceiveAction.Present | ReceiveAction.Deliver;

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

        ReceiveAction actions = reduction.Decision.Transition switch
        {
            ReceiveStreamTransition.None when observation.Kind == ReceiveSignalKind.Metadata
                => DeliveryActions,
            ReceiveStreamTransition.None => ReceiveAction.None,
            ReceiveStreamTransition.IgnoredLate => ReceiveAction.Present,
            _ => DeliveryActions
        };
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
