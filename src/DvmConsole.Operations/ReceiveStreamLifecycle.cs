using System.Collections.Immutable;

namespace DvmConsole.Operations;

public enum ReceiveStreamTransition
{
    None,
    IgnoredLate,
    Started,
    Restarted,
    Continued,
    Resumed,
    Colliding,
    GraceStarted,
    GraceExpired,
    TerminationPending,
    TerminationExpired
}

public readonly record struct ReceiveStreamDecision(
    ReceiveStreamTransition Transition,
    uint? ActiveStreamId = null,
    uint? EndedStreamId = null,
    DateTimeOffset? EndedAt = null)
{
    public bool AcceptTraffic => Transition is not (
        ReceiveStreamTransition.None or
        ReceiveStreamTransition.IgnoredLate or
        ReceiveStreamTransition.TerminationPending);
}

public sealed class ReceiveStreamPolicy
{
    public ReceiveStreamPolicy(
        TimeSpan inactivityTimeout,
        TimeSpan gracePeriod,
        TimeSpan terminatorHold,
        TimeSpan tombstoneLifetime)
    {
        if (inactivityTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(inactivityTimeout));
        if (gracePeriod <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(gracePeriod));
        if (terminatorHold <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(terminatorHold));
        if (tombstoneLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(tombstoneLifetime));

        InactivityTimeout = inactivityTimeout;
        GracePeriod = gracePeriod;
        TerminatorHold = terminatorHold;
        TombstoneLifetime = tombstoneLifetime;
    }

    public static ReceiveStreamPolicy Default { get; } = new(
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5));

    public TimeSpan InactivityTimeout { get; }
    public TimeSpan GracePeriod { get; }
    public TimeSpan TerminatorHold { get; }
    public TimeSpan TombstoneLifetime { get; }
}

public readonly record struct ReceiveStreamActivity(
    DateTimeOffset LastActivity,
    DateTimeOffset? GraceDeadline,
    DateTimeOffset? TerminationDeadline,
    DateTimeOffset? PendingEndAt,
    long InsertionOrder);

/// <summary>
/// Immutable state for the receive stream lifecycle. Active stream identities,
/// timing state, and late-packet tombstones can be captured at ingress and
/// replayed without consulting presentation objects.
/// </summary>
public sealed record ReceiveStreamState(
    uint? PrimaryStreamId,
    ImmutableDictionary<uint, ReceiveStreamActivity> ActiveStreams,
    ImmutableHashSet<uint> ActiveStreamIds,
    ImmutableDictionary<uint, DateTimeOffset> Tombstones,
    long NextInsertionOrder)
{
    public static ReceiveStreamState Empty { get; } = new(
        null,
        ImmutableDictionary<uint, ReceiveStreamActivity>.Empty,
        ImmutableHashSet<uint>.Empty,
        ImmutableDictionary<uint, DateTimeOffset>.Empty,
        0);

    public bool IsActive(uint streamId) => ActiveStreams.ContainsKey(streamId);
}

public readonly record struct ReceiveStreamReduction(
    ReceiveStreamState State,
    ReceiveStreamDecision Decision);

/// <summary>
/// Pure reducer for collision, inactivity grace, pending termination, stream
/// reuse, and tombstone behavior. It owns no clocks, threads, UI, or I/O.
/// </summary>
public static class ReceiveStreamReducer
{
    public static ReceiveStreamReduction ObserveVoice(
        ReceiveStreamState state,
        uint streamId,
        DateTimeOffset now,
        ReceiveStreamPolicy policy)
    {
        Validate(state, streamId, policy);
        state = PurgeTombstones(state, now);
        if (state.Tombstones.ContainsKey(streamId))
        {
            return Result(
                state,
                new ReceiveStreamDecision(
                    ReceiveStreamTransition.IgnoredLate,
                    state.PrimaryStreamId));
        }

        if (state.ActiveStreams.TryGetValue(
                streamId,
                out ReceiveStreamActivity activity))
        {
            bool resumed = activity.GraceDeadline is not null ||
                           activity.TerminationDeadline is not null;
            activity = activity with
            {
                LastActivity = now,
                GraceDeadline = null,
                TerminationDeadline = activity.TerminationDeadline is null
                    ? null
                    : now + policy.TerminatorHold,
                PendingEndAt = activity.TerminationDeadline is null
                    ? activity.PendingEndAt
                    : now
            };
            state = state with
            {
                ActiveStreams = state.ActiveStreams.SetItem(streamId, activity),
                PrimaryStreamId = state.PrimaryStreamId ?? streamId
            };
            return Result(
                state,
                new ReceiveStreamDecision(
                    streamId == state.PrimaryStreamId
                        ? resumed
                            ? ReceiveStreamTransition.Resumed
                            : ReceiveStreamTransition.Continued
                        : ReceiveStreamTransition.Colliding,
                    state.PrimaryStreamId));
        }

        if (state.PrimaryStreamId is null)
        {
            state = AddStream(state, streamId, now, makePrimary: true);
            return Result(
                state,
                new ReceiveStreamDecision(ReceiveStreamTransition.Started, streamId));
        }

        state = AddStream(state, streamId, now, makePrimary: false);
        return Result(
            state,
            new ReceiveStreamDecision(
                ReceiveStreamTransition.Colliding,
                state.PrimaryStreamId));
    }

    public static ReceiveStreamReduction ObserveDefinitiveStart(
        ReceiveStreamState state,
        uint streamId,
        DateTimeOffset now,
        ReceiveStreamPolicy policy)
    {
        Validate(state, streamId, policy);
        state = PurgeTombstones(state, now);
        state = state with
        {
            Tombstones = state.Tombstones.Remove(streamId)
        };

        if (state.ActiveStreams.TryGetValue(
                streamId,
                out ReceiveStreamActivity activity))
        {
            if (activity.TerminationDeadline is not null)
            {
                DateTimeOffset endedAt = activity.PendingEndAt ?? activity.LastActivity;
                state = RemoveStreamWithoutTombstone(state, streamId);
                state = AddStream(
                    state,
                    streamId,
                    now,
                    makePrimary: state.PrimaryStreamId is null);
                return Result(
                    state,
                    new ReceiveStreamDecision(
                        ReceiveStreamTransition.Restarted,
                        state.PrimaryStreamId,
                        streamId,
                        endedAt));
            }

            bool resumed = activity.GraceDeadline is not null;
            activity = activity with
            {
                LastActivity = now,
                GraceDeadline = null
            };
            state = state with
            {
                ActiveStreams = state.ActiveStreams.SetItem(streamId, activity),
                PrimaryStreamId = state.PrimaryStreamId ?? streamId
            };
            return Result(
                state,
                new ReceiveStreamDecision(
                    streamId == state.PrimaryStreamId
                        ? resumed
                            ? ReceiveStreamTransition.Resumed
                            : ReceiveStreamTransition.Continued
                        : ReceiveStreamTransition.Colliding,
                    state.PrimaryStreamId));
        }

        state = AddStream(
            state,
            streamId,
            now,
            makePrimary: state.PrimaryStreamId is null);
        return Result(
            state,
            new ReceiveStreamDecision(
                state.PrimaryStreamId == streamId
                    ? ReceiveStreamTransition.Started
                    : ReceiveStreamTransition.Colliding,
                state.PrimaryStreamId));
    }

    public static ReceiveStreamReduction ObserveTerminator(
        ReceiveStreamState state,
        uint streamId,
        DateTimeOffset now,
        ReceiveStreamPolicy policy)
    {
        Validate(state, streamId, policy);
        state = PurgeTombstones(state, now);
        if (state.Tombstones.ContainsKey(streamId))
        {
            return Result(
                state,
                new ReceiveStreamDecision(
                    ReceiveStreamTransition.IgnoredLate,
                    state.PrimaryStreamId));
        }
        if (!state.ActiveStreams.TryGetValue(
                streamId,
                out ReceiveStreamActivity activity))
        {
            return Result(
                state,
                new ReceiveStreamDecision(
                    ReceiveStreamTransition.None,
                    state.PrimaryStreamId));
        }

        activity = activity with
        {
            TerminationDeadline = activity.TerminationDeadline ?? now + policy.TerminatorHold,
            PendingEndAt = activity.PendingEndAt ?? now
        };
        state = state with
        {
            ActiveStreams = state.ActiveStreams.SetItem(streamId, activity)
        };
        if (state.PrimaryStreamId == streamId)
        {
            state = state with
            {
                PrimaryStreamId = MostRecentlyPresentableStreamId(state.ActiveStreams)
            };
        }
        return Result(
            state,
            new ReceiveStreamDecision(
                ReceiveStreamTransition.TerminationPending,
                state.PrimaryStreamId));
    }

    public static ReceiveStreamReduction Complete(
        ReceiveStreamState state,
        uint streamId,
        DateTimeOffset now,
        ReceiveStreamPolicy policy)
    {
        Validate(state, streamId, policy);
        state = PurgeTombstones(state, now);
        if (!state.ActiveStreams.TryGetValue(
                streamId,
                out ReceiveStreamActivity activity))
        {
            return Result(
                state,
                new ReceiveStreamDecision(
                    ReceiveStreamTransition.None,
                    state.PrimaryStreamId));
        }

        DateTimeOffset endedAt = activity.PendingEndAt ?? activity.LastActivity;
        state = End(state, streamId, now, policy);
        return Result(
            state,
            new ReceiveStreamDecision(
                ReceiveStreamTransition.TerminationExpired,
                state.PrimaryStreamId,
                streamId,
                endedAt));
    }

    public static ReceiveStreamReduction Advance(
        ReceiveStreamState state,
        DateTimeOffset now,
        ReceiveStreamPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(policy);
        state = PurgeTombstones(state, now);
        if (state.ActiveStreams.Count == 0)
            return Result(state, default);

        foreach ((uint streamId, ReceiveStreamActivity activity) in state.ActiveStreams
                     .OrderBy(pair => pair.Value.LastActivity)
                     .ThenBy(pair => pair.Value.InsertionOrder))
        {
            if (activity.TerminationDeadline is DateTimeOffset terminationDeadline)
            {
                if (now < terminationDeadline)
                    continue;

                DateTimeOffset endedAt = activity.PendingEndAt ?? activity.LastActivity;
                state = End(state, streamId, now, policy);
                return Result(
                    state,
                    new ReceiveStreamDecision(
                        ReceiveStreamTransition.TerminationExpired,
                        state.PrimaryStreamId,
                        streamId,
                        endedAt));
            }

            DateTimeOffset inactivityDeadline = activity.LastActivity + policy.InactivityTimeout;
            if (activity.GraceDeadline is null)
            {
                if (now < inactivityDeadline)
                    continue;

                DateTimeOffset graceDeadline = inactivityDeadline + policy.GracePeriod;
                if (now < graceDeadline)
                {
                    ReceiveStreamActivity waiting = activity with
                    {
                        GraceDeadline = graceDeadline
                    };
                    state = state with
                    {
                        ActiveStreams = state.ActiveStreams.SetItem(streamId, waiting)
                    };
                    return Result(
                        state,
                        new ReceiveStreamDecision(
                            ReceiveStreamTransition.GraceStarted,
                            state.PrimaryStreamId));
                }

                state = End(state, streamId, now, policy);
                return Result(
                    state,
                    new ReceiveStreamDecision(
                        ReceiveStreamTransition.GraceExpired,
                        state.PrimaryStreamId,
                        streamId));
            }

            if (now < activity.GraceDeadline.Value)
                continue;

            state = End(state, streamId, now, policy);
            return Result(
                state,
                new ReceiveStreamDecision(
                    ReceiveStreamTransition.GraceExpired,
                    state.PrimaryStreamId,
                    streamId));
        }

        return Result(state, default);
    }

    public static ReceiveStreamState AssumeActive(
        ReceiveStreamState state,
        uint streamId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (streamId == 0)
            throw new ArgumentOutOfRangeException(nameof(streamId));
        if (state.IsActive(streamId))
            return state;

        state = state with { Tombstones = state.Tombstones.Remove(streamId) };
        return AddStream(
            state,
            streamId,
            now,
            makePrimary: state.PrimaryStreamId is null);
    }

    private static ReceiveStreamState AddStream(
        ReceiveStreamState state,
        uint streamId,
        DateTimeOffset now,
        bool makePrimary)
    {
        var activity = new ReceiveStreamActivity(
            now,
            GraceDeadline: null,
            TerminationDeadline: null,
            PendingEndAt: null,
            state.NextInsertionOrder);
        return state with
        {
            PrimaryStreamId = makePrimary ? streamId : state.PrimaryStreamId,
            ActiveStreams = state.ActiveStreams.Add(streamId, activity),
            ActiveStreamIds = state.ActiveStreamIds.Add(streamId),
            NextInsertionOrder = checked(state.NextInsertionOrder + 1)
        };
    }

    private static ReceiveStreamState RemoveStreamWithoutTombstone(
        ReceiveStreamState state,
        uint streamId)
    {
        ImmutableDictionary<uint, ReceiveStreamActivity> remaining =
            state.ActiveStreams.Remove(streamId);
        return state with
        {
            ActiveStreams = remaining,
            ActiveStreamIds = state.ActiveStreamIds.Remove(streamId),
            PrimaryStreamId = state.PrimaryStreamId == streamId
                ? MostRecentlyPresentableStreamId(remaining)
                : state.PrimaryStreamId
        };
    }

    private static ReceiveStreamState End(
        ReceiveStreamState state,
        uint streamId,
        DateTimeOffset now,
        ReceiveStreamPolicy policy)
    {
        state = RemoveStreamWithoutTombstone(state, streamId);
        return state with
        {
            Tombstones = state.Tombstones.SetItem(
                streamId,
                now + policy.TombstoneLifetime)
        };
    }

    private static uint? MostRecentlyPresentableStreamId(
        ImmutableDictionary<uint, ReceiveStreamActivity> streams)
        => streams
            .Where(pair => pair.Value.TerminationDeadline is null)
            .OrderByDescending(pair => pair.Value.LastActivity)
            .ThenBy(pair => pair.Value.InsertionOrder)
            .Select(pair => (uint?)pair.Key)
            .FirstOrDefault();

    private static ReceiveStreamState PurgeTombstones(
        ReceiveStreamState state,
        DateTimeOffset now)
    {
        ImmutableDictionary<uint, DateTimeOffset>.Builder? retained = null;
        foreach ((uint streamId, DateTimeOffset expiresAt) in state.Tombstones)
        {
            if (expiresAt > now)
                continue;
            retained ??= state.Tombstones.ToBuilder();
            retained.Remove(streamId);
        }
        return retained is null
            ? state
            : state with { Tombstones = retained.ToImmutable() };
    }

    private static ReceiveStreamReduction Result(
        ReceiveStreamState state,
        ReceiveStreamDecision decision)
        => new(state, decision);

    private static void Validate(
        ReceiveStreamState state,
        uint streamId,
        ReceiveStreamPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(policy);
        if (streamId == 0)
            throw new ArgumentOutOfRangeException(nameof(streamId));
    }
}

/// <summary>
/// Compatibility state holder for decoder/session consumers. All behavior is
/// delegated to the immutable reducer above; presentation code should carry a
/// captured reduction instead of owning this wrapper.
/// </summary>
public sealed class ReceiveStreamLifecycle
{
    private readonly ReceiveStreamPolicy policy;
    private ReceiveStreamState state = ReceiveStreamState.Empty;

    public ReceiveStreamLifecycle(
        TimeSpan inactivityTimeout,
        TimeSpan gracePeriod,
        TimeSpan terminatorHold,
        TimeSpan tombstoneLifetime)
        : this(new ReceiveStreamPolicy(
            inactivityTimeout,
            gracePeriod,
            terminatorHold,
            tombstoneLifetime))
    {
    }

    private ReceiveStreamLifecycle(ReceiveStreamPolicy policy)
        => this.policy = policy;

    public static ReceiveStreamLifecycle CreateDefault()
        => new(ReceiveStreamPolicy.Default);

    public uint? ActiveStreamId => state.PrimaryStreamId;
    public ReceiveStreamState Snapshot => state;
    public bool IsActive(uint streamId) => state.IsActive(streamId);

    public ReceiveStreamDecision ObserveVoice(uint streamId, DateTimeOffset now)
        => Apply(ReceiveStreamReducer.ObserveVoice(state, streamId, now, policy));

    public ReceiveStreamDecision ObserveDefinitiveStart(uint streamId, DateTimeOffset now)
        => Apply(ReceiveStreamReducer.ObserveDefinitiveStart(state, streamId, now, policy));

    public ReceiveStreamDecision ObserveTerminator(uint streamId, DateTimeOffset now)
        => Apply(ReceiveStreamReducer.ObserveTerminator(state, streamId, now, policy));

    public ReceiveStreamDecision Complete(uint streamId, DateTimeOffset now)
        => Apply(ReceiveStreamReducer.Complete(state, streamId, now, policy));

    public ReceiveStreamDecision Advance(DateTimeOffset now)
        => Apply(ReceiveStreamReducer.Advance(state, now, policy));

    private ReceiveStreamDecision Apply(ReceiveStreamReduction reduction)
    {
        state = reduction.State;
        return reduction.Decision;
    }
}
