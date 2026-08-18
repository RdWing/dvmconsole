namespace DvmConsole.Desktop;

internal enum ReceiveStreamTransition
{
    None,
    IgnoredLate,
    Started,
    Continued,
    Resumed,
    Ended,
    Superseded,
    GraceStarted,
    GraceExpired
}

internal readonly record struct ReceiveStreamDecision(
    ReceiveStreamTransition Transition,
    uint? ActiveStreamId = null,
    uint? EndedStreamId = null)
{
    public bool AcceptTraffic => Transition is not (ReceiveStreamTransition.None or ReceiveStreamTransition.IgnoredLate);
}

internal sealed class ReceiveStreamLifecycle
{
    private readonly TimeSpan inactivityTimeout;
    private readonly TimeSpan gracePeriod;
    private readonly TimeSpan tombstoneLifetime;
    private readonly Dictionary<uint, DateTimeOffset> tombstones = [];
    private DateTimeOffset? lastActivity;
    private DateTimeOffset? graceDeadline;

    public ReceiveStreamLifecycle(
        TimeSpan inactivityTimeout,
        TimeSpan gracePeriod,
        TimeSpan tombstoneLifetime)
    {
        if (inactivityTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(inactivityTimeout));
        if (gracePeriod <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(gracePeriod));
        if (tombstoneLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(tombstoneLifetime));

        this.inactivityTimeout = inactivityTimeout;
        this.gracePeriod = gracePeriod;
        this.tombstoneLifetime = tombstoneLifetime;
    }

    public uint? ActiveStreamId { get; private set; }

    public ReceiveStreamDecision ObserveVoice(uint streamId, DateTimeOffset now)
    {
        if (streamId == 0)
            throw new ArgumentOutOfRangeException(nameof(streamId));

        PurgeTombstones(now);
        ExpireActiveIfPastGrace(now);
        if (tombstones.ContainsKey(streamId))
            return new ReceiveStreamDecision(ReceiveStreamTransition.IgnoredLate, ActiveStreamId);

        if (ActiveStreamId is not uint activeStreamId)
        {
            Start(streamId, now);
            return new ReceiveStreamDecision(ReceiveStreamTransition.Started, streamId);
        }

        if (activeStreamId == streamId)
        {
            bool resumed = graceDeadline is not null;
            lastActivity = now;
            graceDeadline = null;
            return new ReceiveStreamDecision(
                resumed ? ReceiveStreamTransition.Resumed : ReceiveStreamTransition.Continued,
                streamId);
        }

        Tombstone(activeStreamId, now);
        Start(streamId, now);
        return new ReceiveStreamDecision(
            ReceiveStreamTransition.Superseded,
            streamId,
            activeStreamId);
    }

    public ReceiveStreamDecision ObserveTerminator(uint streamId, DateTimeOffset now)
    {
        if (streamId == 0)
            throw new ArgumentOutOfRangeException(nameof(streamId));

        PurgeTombstones(now);
        ExpireActiveIfPastGrace(now);
        if (tombstones.ContainsKey(streamId))
            return new ReceiveStreamDecision(ReceiveStreamTransition.IgnoredLate, ActiveStreamId);
        if (ActiveStreamId != streamId)
            return new ReceiveStreamDecision(ReceiveStreamTransition.None, ActiveStreamId);

        EndActive(now);
        return new ReceiveStreamDecision(ReceiveStreamTransition.Ended, EndedStreamId: streamId);
    }

    public ReceiveStreamDecision Advance(DateTimeOffset now)
    {
        PurgeTombstones(now);
        if (ActiveStreamId is not uint activeStreamId || lastActivity is not DateTimeOffset activity)
            return default;

        DateTimeOffset inactivityDeadline = activity + inactivityTimeout;
        if (graceDeadline is null)
        {
            if (now < inactivityDeadline)
                return default;

            DateTimeOffset deadline = inactivityDeadline + gracePeriod;
            if (now >= deadline)
            {
                EndActive(now);
                return new ReceiveStreamDecision(
                    ReceiveStreamTransition.GraceExpired,
                    EndedStreamId: activeStreamId);
            }

            graceDeadline = deadline;
            return new ReceiveStreamDecision(ReceiveStreamTransition.GraceStarted, activeStreamId);
        }

        if (now < graceDeadline.Value)
            return default;

        EndActive(now);
        return new ReceiveStreamDecision(
            ReceiveStreamTransition.GraceExpired,
            EndedStreamId: activeStreamId);
    }

    private void Start(uint streamId, DateTimeOffset now)
    {
        ActiveStreamId = streamId;
        lastActivity = now;
        graceDeadline = null;
    }

    private void EndActive(DateTimeOffset now)
    {
        if (ActiveStreamId is uint activeStreamId)
            Tombstone(activeStreamId, now);
        ActiveStreamId = null;
        lastActivity = null;
        graceDeadline = null;
    }

    private void ExpireActiveIfPastGrace(DateTimeOffset now)
    {
        if (ActiveStreamId is null || lastActivity is not DateTimeOffset activity)
            return;
        if (now >= activity + inactivityTimeout + gracePeriod)
            EndActive(now);
    }

    private void Tombstone(uint streamId, DateTimeOffset now)
        => tombstones[streamId] = now + tombstoneLifetime;

    private void PurgeTombstones(DateTimeOffset now)
    {
        foreach (uint streamId in tombstones
                     .Where(pair => pair.Value <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            tombstones.Remove(streamId);
        }
    }
}
