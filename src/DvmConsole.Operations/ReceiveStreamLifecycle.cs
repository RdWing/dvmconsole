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
    public const int DefaultMaximumTrackedStreams = 32;

    public ReceiveStreamPolicy(
        TimeSpan inactivityTimeout,
        TimeSpan gracePeriod,
        TimeSpan terminatorHold,
        TimeSpan tombstoneLifetime,
        int maximumTrackedStreams = DefaultMaximumTrackedStreams)
    {
        if (inactivityTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(inactivityTimeout));
        if (gracePeriod <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(gracePeriod));
        if (terminatorHold <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(terminatorHold));
        if (tombstoneLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(tombstoneLifetime));
        if (maximumTrackedStreams <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumTrackedStreams));

        InactivityTimeout = inactivityTimeout;
        GracePeriod = gracePeriod;
        TerminatorHold = terminatorHold;
        TombstoneLifetime = tombstoneLifetime;
        MaximumTrackedStreams = maximumTrackedStreams;
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
    public int MaximumTrackedStreams { get; }
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
        => Apply(state, policy, machine => machine.ObserveVoice(streamId, now));

    public static ReceiveStreamReduction ObserveDefinitiveStart(
        ReceiveStreamState state,
        uint streamId,
        DateTimeOffset now,
        ReceiveStreamPolicy policy)
        => Apply(state, policy, machine => machine.ObserveDefinitiveStart(streamId, now));

    public static ReceiveStreamReduction ObserveTerminator(
        ReceiveStreamState state,
        uint streamId,
        DateTimeOffset now,
        ReceiveStreamPolicy policy)
        => Apply(state, policy, machine => machine.ObserveTerminator(streamId, now));

    public static ReceiveStreamReduction Complete(
        ReceiveStreamState state,
        uint streamId,
        DateTimeOffset now,
        ReceiveStreamPolicy policy)
        => Apply(state, policy, machine => machine.Complete(streamId, now));

    public static ReceiveStreamReduction Advance(
        ReceiveStreamState state,
        DateTimeOffset now,
        ReceiveStreamPolicy policy)
        => Apply(state, policy, machine => machine.Advance(now));

    public static ReceiveStreamState AssumeActive(
        ReceiveStreamState state,
        uint streamId,
        DateTimeOffset now,
        ReceiveStreamPolicy? policy = null)
    {
        var machine = new ReceiveStreamStateMachine(
            policy ?? ReceiveStreamPolicy.Default,
            state);
        machine.AssumeActive(streamId, now);
        return machine.Snapshot;
    }

    private static ReceiveStreamReduction Apply(
        ReceiveStreamState state,
        ReceiveStreamPolicy policy,
        Func<ReceiveStreamStateMachine, ReceiveStreamDecision> transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        var machine = new ReceiveStreamStateMachine(policy, state);
        ReceiveStreamDecision decision = transition(machine);
        return new ReceiveStreamReduction(machine.Snapshot, decision);
    }
}

/// <summary>
/// Compatibility state holder for decoder/session consumers. One wrapper owns
/// one mutable lifecycle engine; callers can request an immutable snapshot for
/// diagnostics or replay without paying that cost on every voice packet.
/// </summary>
public sealed class ReceiveStreamLifecycle
{
    private readonly ReceiveStreamStateMachine stateMachine;

    public ReceiveStreamLifecycle(
        TimeSpan inactivityTimeout,
        TimeSpan gracePeriod,
        TimeSpan terminatorHold,
        TimeSpan tombstoneLifetime,
        int maximumTrackedStreams = ReceiveStreamPolicy.DefaultMaximumTrackedStreams)
        : this(new ReceiveStreamPolicy(
            inactivityTimeout,
            gracePeriod,
            terminatorHold,
            tombstoneLifetime,
            maximumTrackedStreams))
    {
    }

    private ReceiveStreamLifecycle(ReceiveStreamPolicy policy)
        => stateMachine = new ReceiveStreamStateMachine(policy);

    public static ReceiveStreamLifecycle CreateDefault()
        => new(ReceiveStreamPolicy.Default);

    public uint? ActiveStreamId => stateMachine.PrimaryStreamId;
    public ReceiveStreamState Snapshot => stateMachine.Snapshot;
    public bool IsActive(uint streamId) => stateMachine.IsActive(streamId);

    public ReceiveStreamDecision ObserveVoice(uint streamId, DateTimeOffset now)
        => stateMachine.ObserveVoice(streamId, now);

    public ReceiveStreamDecision ObserveDefinitiveStart(uint streamId, DateTimeOffset now)
        => stateMachine.ObserveDefinitiveStart(streamId, now);

    public ReceiveStreamDecision ObserveTerminator(uint streamId, DateTimeOffset now)
        => stateMachine.ObserveTerminator(streamId, now);

    public ReceiveStreamDecision Complete(uint streamId, DateTimeOffset now)
        => stateMachine.Complete(streamId, now);

    public ReceiveStreamDecision Advance(DateTimeOffset now)
        => stateMachine.Advance(now);
}
