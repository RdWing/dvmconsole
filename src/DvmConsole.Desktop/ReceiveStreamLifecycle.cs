namespace DvmConsole.Desktop;

internal enum ReceiveStreamTransition
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

internal readonly record struct ReceiveStreamDecision(
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

internal sealed class ReceiveStreamLifecycle
{
    private static readonly TimeSpan DefaultInactivityTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultTerminatorHold = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultTombstoneLifetime = TimeSpan.FromSeconds(5);
    private readonly TimeSpan inactivityTimeout;
    private readonly TimeSpan gracePeriod;
    private readonly TimeSpan terminatorHold;
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

        this.inactivityTimeout = inactivityTimeout;
        this.gracePeriod = gracePeriod;
        this.terminatorHold = terminatorHold;
        this.tombstoneLifetime = tombstoneLifetime;
    }

    public static ReceiveStreamLifecycle CreateDefault()
        => new(
            DefaultInactivityTimeout,
            DefaultGracePeriod,
            DefaultTerminatorHold,
            DefaultTombstoneLifetime);

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
            bool resumed = activity.GraceDeadline is not null ||
                           activity.TerminationDeadline is not null;
            activity.LastActivity = now;
            activity.GraceDeadline = null;
            if (activity.TerminationDeadline is not null)
            {
                // The terminator arrived ahead of voice that was already in
                // flight. Retain the terminator intent, but wait for a quiet
                // interval after the newest delayed voice before finalizing.
                activity.TerminationDeadline = now + terminatorHold;
                activity.PendingEndAt = now;
            }
            if (primaryStreamId is null)
                primaryStreamId = streamId;
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
            if (activity.TerminationDeadline is not null)
            {
                DateTimeOffset endedAt = activity.PendingEndAt ?? activity.LastActivity;
                activeStreams.Remove(streamId);
                if (primaryStreamId == streamId)
                    primaryStreamId = MostRecentlyPresentableStreamId();
                if (primaryStreamId is null)
                    Start(streamId, now);
                else
                    activeStreams.Add(streamId, new StreamActivity(now));
                return new ReceiveStreamDecision(
                    ReceiveStreamTransition.Restarted,
                    primaryStreamId,
                    streamId,
                    endedAt);
            }

            bool resumed = activity.GraceDeadline is not null;
            activity.LastActivity = now;
            activity.GraceDeadline = null;
            if (primaryStreamId is null)
                primaryStreamId = streamId;
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
        if (!activeStreams.TryGetValue(streamId, out StreamActivity? activity))
            return new ReceiveStreamDecision(ReceiveStreamTransition.None, ActiveStreamId);

        activity.TerminationDeadline ??= now + terminatorHold;
        activity.PendingEndAt ??= now;
        if (primaryStreamId == streamId)
            primaryStreamId = MostRecentlyPresentableStreamId();
        return new ReceiveStreamDecision(
            ReceiveStreamTransition.TerminationPending,
            primaryStreamId);
    }

    // Used after the presentation lifecycle has observed the bounded hold.
    // This keeps independently-owned decoder state aligned without starting a
    // second hold interval.
    public ReceiveStreamDecision Complete(uint streamId, DateTimeOffset now)
    {
        if (streamId == 0)
            throw new ArgumentOutOfRangeException(nameof(streamId));

        PurgeTombstones(now);
        if (!activeStreams.TryGetValue(streamId, out StreamActivity? activity))
            return new ReceiveStreamDecision(ReceiveStreamTransition.None, ActiveStreamId);

        DateTimeOffset endedAt = activity.PendingEndAt ?? activity.LastActivity;
        End(streamId, now);
        return new ReceiveStreamDecision(
            ReceiveStreamTransition.TerminationExpired,
            primaryStreamId,
            streamId,
            endedAt);
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
            if (activity.TerminationDeadline is DateTimeOffset terminationDeadline)
            {
                if (now < terminationDeadline)
                    continue;

                DateTimeOffset endedAt = activity.PendingEndAt ?? activity.LastActivity;
                End(streamId, now);
                return new ReceiveStreamDecision(
                    ReceiveStreamTransition.TerminationExpired,
                    primaryStreamId,
                    streamId,
                    endedAt);
            }

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
            primaryStreamId = MostRecentlyPresentableStreamId();
    }

    private uint? MostRecentlyPresentableStreamId()
        => activeStreams
            .Where(pair => pair.Value.TerminationDeadline is null)
            .OrderByDescending(pair => pair.Value.LastActivity)
            .Select(pair => (uint?)pair.Key)
            .FirstOrDefault();

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
        public DateTimeOffset? TerminationDeadline { get; set; }
        public DateTimeOffset? PendingEndAt { get; set; }
    }
}
