using System.Collections.Immutable;

namespace DvmConsole.Operations;

/// <summary>
/// Mutable receive-lifecycle state with one explicit owner. The owner supplies
/// synchronization and time; this type owns no threads, UI, decoder, or I/O.
/// Immutable membership snapshots are rebuilt only when streams enter or leave.
/// </summary>
internal sealed class ReceiveStreamStateMachine
{
    private readonly ReceiveStreamPolicy policy;
    private readonly Dictionary<uint, ReceiveStreamActivity> activeStreams = [];
    private readonly Dictionary<uint, DateTimeOffset> tombstones = [];
    private ImmutableHashSet<uint> activeStreamIds = ImmutableHashSet<uint>.Empty;
    private uint? primaryStreamId;
    private long nextInsertionOrder;
    private bool activeStreamIdsChanged;

    public ReceiveStreamStateMachine(ReceiveStreamPolicy policy)
        : this(policy, ReceiveStreamState.Empty)
    {
    }

    internal ReceiveStreamStateMachine(
        ReceiveStreamPolicy policy,
        ReceiveStreamState initialState)
    {
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
        ArgumentNullException.ThrowIfNull(initialState);
        foreach ((uint streamId, ReceiveStreamActivity activity) in initialState.ActiveStreams)
            activeStreams.Add(streamId, activity);
        foreach ((uint streamId, DateTimeOffset expiresAt) in initialState.Tombstones)
            tombstones.Add(streamId, expiresAt);
        activeStreamIds = initialState.ActiveStreamIds;
        primaryStreamId = initialState.PrimaryStreamId;
        nextInsertionOrder = initialState.NextInsertionOrder;
    }

    public uint? PrimaryStreamId => primaryStreamId;

    public ImmutableHashSet<uint> ActiveStreamIds
    {
        get
        {
            if (!activeStreamIdsChanged)
                return activeStreamIds;

            activeStreamIds = activeStreams.Keys.ToImmutableHashSet();
            activeStreamIdsChanged = false;
            return activeStreamIds;
        }
    }

    public ReceiveStreamState Snapshot => new(
        primaryStreamId,
        activeStreams.ToImmutableDictionary(),
        ActiveStreamIds,
        tombstones.ToImmutableDictionary(),
        nextInsertionOrder);

    public bool IsActive(uint streamId) => activeStreams.ContainsKey(streamId);

    public bool HasLiveTombstone(uint streamId, DateTimeOffset now)
    {
        PurgeTombstones(now);
        return tombstones.TryGetValue(streamId, out DateTimeOffset expiresAt) &&
               expiresAt > now;
    }

    public ReceiveStreamDecision ObserveVoice(uint streamId, DateTimeOffset now)
    {
        ValidateStreamId(streamId);
        PurgeTombstones(now);
        if (tombstones.ContainsKey(streamId))
            return new ReceiveStreamDecision(ReceiveStreamTransition.IgnoredLate, primaryStreamId);

        if (activeStreams.TryGetValue(streamId, out ReceiveStreamActivity activity))
        {
            bool resumed = activity.GraceDeadline is not null ||
                           activity.TerminationDeadline is not null;
            activeStreams[streamId] = activity with
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
            primaryStreamId ??= streamId;
            return new ReceiveStreamDecision(
                streamId == primaryStreamId
                    ? resumed
                        ? ReceiveStreamTransition.Resumed
                        : ReceiveStreamTransition.Continued
                    : ReceiveStreamTransition.Colliding,
                primaryStreamId);
        }

        if (primaryStreamId is null)
        {
            AddStream(streamId, now, makePrimary: true);
            return new ReceiveStreamDecision(ReceiveStreamTransition.Started, streamId);
        }

        AddStream(streamId, now, makePrimary: false);
        return new ReceiveStreamDecision(ReceiveStreamTransition.Colliding, primaryStreamId);
    }

    public ReceiveStreamDecision ObserveDefinitiveStart(uint streamId, DateTimeOffset now)
    {
        ValidateStreamId(streamId);
        PurgeTombstones(now);
        tombstones.Remove(streamId);

        if (activeStreams.TryGetValue(streamId, out ReceiveStreamActivity activity))
        {
            if (activity.TerminationDeadline is not null)
            {
                DateTimeOffset endedAt = activity.PendingEndAt ?? activity.LastActivity;
                RemoveStreamWithoutTombstone(streamId);
                AddStream(streamId, now, makePrimary: primaryStreamId is null);
                return new ReceiveStreamDecision(
                    ReceiveStreamTransition.Restarted,
                    primaryStreamId,
                    streamId,
                    endedAt);
            }

            bool resumed = activity.GraceDeadline is not null;
            activeStreams[streamId] = activity with
            {
                LastActivity = now,
                GraceDeadline = null
            };
            primaryStreamId ??= streamId;
            return new ReceiveStreamDecision(
                streamId == primaryStreamId
                    ? resumed
                        ? ReceiveStreamTransition.Resumed
                        : ReceiveStreamTransition.Continued
                    : ReceiveStreamTransition.Colliding,
                primaryStreamId);
        }

        AddStream(streamId, now, makePrimary: primaryStreamId is null);
        return new ReceiveStreamDecision(
            primaryStreamId == streamId
                ? ReceiveStreamTransition.Started
                : ReceiveStreamTransition.Colliding,
            primaryStreamId);
    }

    public ReceiveStreamDecision ObserveTerminator(uint streamId, DateTimeOffset now)
    {
        ValidateStreamId(streamId);
        PurgeTombstones(now);
        if (tombstones.ContainsKey(streamId))
            return new ReceiveStreamDecision(ReceiveStreamTransition.IgnoredLate, primaryStreamId);
        if (!activeStreams.TryGetValue(streamId, out ReceiveStreamActivity activity))
            return new ReceiveStreamDecision(ReceiveStreamTransition.None, primaryStreamId);

        activeStreams[streamId] = activity with
        {
            TerminationDeadline = activity.TerminationDeadline ?? now + policy.TerminatorHold,
            PendingEndAt = activity.PendingEndAt ?? now
        };
        if (primaryStreamId == streamId)
            primaryStreamId = MostRecentlyPresentableStreamId();
        return new ReceiveStreamDecision(ReceiveStreamTransition.TerminationPending, primaryStreamId);
    }

    public ReceiveStreamDecision Complete(uint streamId, DateTimeOffset now)
    {
        ValidateStreamId(streamId);
        PurgeTombstones(now);
        if (!activeStreams.TryGetValue(streamId, out ReceiveStreamActivity activity))
            return new ReceiveStreamDecision(ReceiveStreamTransition.None, primaryStreamId);

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
        bool found = false;
        uint selectedStreamId = 0;
        ReceiveStreamActivity selectedActivity = default;
        foreach ((uint streamId, ReceiveStreamActivity activity) in activeStreams)
        {
            if (!IsDue(activity, now))
                continue;
            if (found && CompareOrder(activity, selectedActivity) >= 0)
                continue;
            found = true;
            selectedStreamId = streamId;
            selectedActivity = activity;
        }

        if (!found)
            return default;

        if (selectedActivity.TerminationDeadline is not null)
        {
            DateTimeOffset endedAt = selectedActivity.PendingEndAt ?? selectedActivity.LastActivity;
            End(selectedStreamId, now);
            return new ReceiveStreamDecision(
                ReceiveStreamTransition.TerminationExpired,
                primaryStreamId,
                selectedStreamId,
                endedAt);
        }

        DateTimeOffset inactivityDeadline = selectedActivity.LastActivity + policy.InactivityTimeout;
        if (selectedActivity.GraceDeadline is null)
        {
            DateTimeOffset graceDeadline = inactivityDeadline + policy.GracePeriod;
            if (now < graceDeadline)
            {
                activeStreams[selectedStreamId] = selectedActivity with
                {
                    GraceDeadline = graceDeadline
                };
                return new ReceiveStreamDecision(ReceiveStreamTransition.GraceStarted, primaryStreamId);
            }
        }

        End(selectedStreamId, now);
        return new ReceiveStreamDecision(
            ReceiveStreamTransition.GraceExpired,
            primaryStreamId,
            selectedStreamId);
    }

    public void AssumeActive(uint streamId, DateTimeOffset now)
    {
        ValidateStreamId(streamId);
        if (IsActive(streamId))
            return;

        tombstones.Remove(streamId);
        AddStream(streamId, now, makePrimary: primaryStreamId is null);
    }

    private void AddStream(uint streamId, DateTimeOffset now, bool makePrimary)
    {
        MakeRoomForStream();
        activeStreams.Add(streamId, new ReceiveStreamActivity(
            now,
            GraceDeadline: null,
            TerminationDeadline: null,
            PendingEndAt: null,
            nextInsertionOrder));
        activeStreamIdsChanged = true;
        if (makePrimary)
            primaryStreamId = streamId;
        nextInsertionOrder = checked(nextInsertionOrder + 1);
    }

    private void MakeRoomForStream()
    {
        if (activeStreams.Count < policy.MaximumTrackedStreams)
            return;

        uint oldestStreamId = activeStreams
            .OrderBy(pair => pair.Value.LastActivity)
            .ThenBy(pair => pair.Value.InsertionOrder)
            .Select(pair => pair.Key)
            .First();
        RemoveStreamWithoutTombstone(oldestStreamId);
    }

    private void RemoveStreamWithoutTombstone(uint streamId)
    {
        if (!activeStreams.Remove(streamId))
            return;

        activeStreamIdsChanged = true;
        if (primaryStreamId == streamId)
            primaryStreamId = MostRecentlyPresentableStreamId();
    }

    private void End(uint streamId, DateTimeOffset now)
    {
        RemoveStreamWithoutTombstone(streamId);
        tombstones[streamId] = now + policy.TombstoneLifetime;
        if (tombstones.Count <= policy.MaximumTrackedStreams)
            return;

        uint oldestTombstone = tombstones
            .OrderBy(pair => pair.Value)
            .Select(pair => pair.Key)
            .First();
        tombstones.Remove(oldestTombstone);
    }

    private uint? MostRecentlyPresentableStreamId()
    {
        uint? selected = null;
        ReceiveStreamActivity selectedActivity = default;
        foreach ((uint streamId, ReceiveStreamActivity activity) in activeStreams)
        {
            if (activity.TerminationDeadline is not null)
                continue;
            if (selected is not null &&
                (activity.LastActivity < selectedActivity.LastActivity ||
                 (activity.LastActivity == selectedActivity.LastActivity &&
                  activity.InsertionOrder >= selectedActivity.InsertionOrder)))
            {
                continue;
            }
            selected = streamId;
            selectedActivity = activity;
        }
        return selected;
    }

    private void PurgeTombstones(DateTimeOffset now)
    {
        while (true)
        {
            uint expiredStreamId = 0;
            bool found = false;
            foreach ((uint streamId, DateTimeOffset expiresAt) in tombstones)
            {
                if (expiresAt > now)
                    continue;
                expiredStreamId = streamId;
                found = true;
                break;
            }
            if (!found)
                return;
            tombstones.Remove(expiredStreamId);
        }
    }

    private bool IsDue(ReceiveStreamActivity activity, DateTimeOffset now)
    {
        if (activity.TerminationDeadline is DateTimeOffset terminationDeadline)
            return now >= terminationDeadline;
        if (activity.GraceDeadline is DateTimeOffset graceDeadline)
            return now >= graceDeadline;
        return now >= activity.LastActivity + policy.InactivityTimeout;
    }

    private static int CompareOrder(
        ReceiveStreamActivity left,
        ReceiveStreamActivity right)
    {
        int activityOrder = left.LastActivity.CompareTo(right.LastActivity);
        return activityOrder != 0
            ? activityOrder
            : left.InsertionOrder.CompareTo(right.InsertionOrder);
    }

    private static void ValidateStreamId(uint streamId)
    {
        if (streamId == 0)
            throw new ArgumentOutOfRangeException(nameof(streamId));
    }
}
