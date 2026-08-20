namespace DvmConsole.Desktop;

internal enum ReceiveStreamTransition
{
    None,
    IgnoredLate,
    Started,
    Continued,
    Resumed,
    Ended,
    Colliding,
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
    // A busy talkgroup can legitimately carry two FNE stream IDs at once.
    // Keep each alive independently; primaryStreamId is presentation policy,
    // not an ownership claim over decode, recording, or call history.
    private readonly Dictionary<uint, StreamActivity> activeStreams = [];
    private uint? primaryStreamId;

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

    public uint? ActiveStreamId => primaryStreamId;

    public bool IsActive(uint streamId) => activeStreams.ContainsKey(streamId);

    public ReceiveStreamDecision ObserveVoice(uint streamId, DateTimeOffset now)
    {
        if (streamId == 0)
            throw new ArgumentOutOfRangeException(nameof(streamId));

        PurgeTombstones(now);
        if (tombstones.ContainsKey(streamId))
            return new ReceiveStreamDecision(ReceiveStreamTransition.IgnoredLate, ActiveStreamId);

        if (activeStreams.TryGetValue(streamId, out StreamActivity? activity))
        {
            bool resumed = activity.GraceDeadline is not null;
            activity.LastActivity = now;
            activity.GraceDeadline = null;
            return new ReceiveStreamDecision(
                streamId == primaryStreamId
                    ? resumed ? ReceiveStreamTransition.Resumed : ReceiveStreamTransition.Continued
                    : ReceiveStreamTransition.Colliding,
                primaryStreamId);
        }

        if (primaryStreamId is null)
        {
            Start(streamId, now);
            return new ReceiveStreamDecision(ReceiveStreamTransition.Started, streamId);
        }

        activeStreams.Add(streamId, new StreamActivity(now));
        return new ReceiveStreamDecision(
            ReceiveStreamTransition.Colliding,
            primaryStreamId);
    }

    // A DMR voice LC header is an unambiguous start-of-call signal. The FNE
    // may reuse a stream ID before the late-packet tombstone expires, so a
    // definitive header is allowed to reopen that ID while ordinary late
    // voice remains suppressed.
    public ReceiveStreamDecision ObserveDefinitiveStart(uint streamId, DateTimeOffset now)
    {
        if (streamId == 0)
            throw new ArgumentOutOfRangeException(nameof(streamId));

        PurgeTombstones(now);
        tombstones.Remove(streamId);

        if (activeStreams.TryGetValue(streamId, out StreamActivity? activity))
        {
            bool resumed = activity.GraceDeadline is not null;
            activity.LastActivity = now;
            activity.GraceDeadline = null;
            return new ReceiveStreamDecision(
                streamId == primaryStreamId
                    ? resumed ? ReceiveStreamTransition.Resumed : ReceiveStreamTransition.Continued
                    : ReceiveStreamTransition.Colliding,
                primaryStreamId);
        }

        if (primaryStreamId is null)
        {
            Start(streamId, now);
            return new ReceiveStreamDecision(ReceiveStreamTransition.Started, streamId);
        }

        activeStreams.Add(streamId, new StreamActivity(now));
        return new ReceiveStreamDecision(
            ReceiveStreamTransition.Colliding,
            primaryStreamId);
    }

    public ReceiveStreamDecision ObserveTerminator(uint streamId, DateTimeOffset now)
    {
        if (streamId == 0)
            throw new ArgumentOutOfRangeException(nameof(streamId));

        PurgeTombstones(now);
        if (tombstones.ContainsKey(streamId))
            return new ReceiveStreamDecision(ReceiveStreamTransition.IgnoredLate, ActiveStreamId);
        if (!activeStreams.Remove(streamId))
            return new ReceiveStreamDecision(ReceiveStreamTransition.None, ActiveStreamId);

        Tombstone(streamId, now);
        if (primaryStreamId == streamId)
            primaryStreamId = MostRecentlyActiveStreamId();
        return new ReceiveStreamDecision(
            ReceiveStreamTransition.Ended,
            primaryStreamId,
            streamId);
    }

    public ReceiveStreamDecision Advance(DateTimeOffset now)
    {
        PurgeTombstones(now);
        if (activeStreams.Count == 0)
            return default;

        foreach ((uint streamId, StreamActivity activity) in activeStreams
                     .OrderBy(pair => pair.Value.LastActivity)
                     .ToArray())
        {
            DateTimeOffset inactivityDeadline = activity.LastActivity + inactivityTimeout;
            if (activity.GraceDeadline is null)
            {
                if (now < inactivityDeadline)
                    continue;

                DateTimeOffset deadline = inactivityDeadline + gracePeriod;
                if (now < deadline)
                {
                    activity.GraceDeadline = deadline;
                    return new ReceiveStreamDecision(ReceiveStreamTransition.GraceStarted, primaryStreamId);
                }

                End(streamId, now);
                return new ReceiveStreamDecision(
                    ReceiveStreamTransition.GraceExpired,
                    primaryStreamId,
                    streamId);
            }

            if (now < activity.GraceDeadline.Value)
                continue;

            End(streamId, now);
            return new ReceiveStreamDecision(
                ReceiveStreamTransition.GraceExpired,
                primaryStreamId,
                streamId);
        }

        return default;
    }

    private void Start(uint streamId, DateTimeOffset now)
    {
        activeStreams.Add(streamId, new StreamActivity(now));
        primaryStreamId = streamId;
    }

    private void End(uint streamId, DateTimeOffset now)
    {
        if (!activeStreams.Remove(streamId))
            return;
        Tombstone(streamId, now);
        if (primaryStreamId == streamId)
            primaryStreamId = MostRecentlyActiveStreamId();
    }

    private uint? MostRecentlyActiveStreamId()
        => activeStreams.Count == 0
            ? null
            : activeStreams.MaxBy(pair => pair.Value.LastActivity).Key;

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

    private sealed class StreamActivity(DateTimeOffset lastActivity)
    {
        public DateTimeOffset LastActivity { get; set; } = lastActivity;
        public DateTimeOffset? GraceDeadline { get; set; }
    }
}
