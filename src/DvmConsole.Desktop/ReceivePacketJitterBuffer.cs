using System.Diagnostics;

namespace DvmConsole.Desktop;

internal readonly record struct ReceiveJitterBufferDequeueMetadata(
    bool ReorderedBeforePlayout,
    int MissingPacketsAtDeadline,
    TimeSpan TargetDelay,
    bool IsAdaptive);

// Reorders packets for independent receive streams and releases them against a
// monotonic playout deadline. Codec-specific loss concealment remains the
// decoder's responsibility after a deadline expires.
internal sealed class ReceivePacketJitterBuffer<T>
{
    private const int SequenceModulus = ushort.MaxValue;
    private const int MaximumForwardDistance = SequenceModulus / 2;

    private readonly LinkedList<BufferedPacket> packets = [];
    private readonly Dictionary<uint, StreamState> streams = [];
    private readonly Func<T, uint> getStreamId;
    private readonly Func<T, ushort> getSequence;
    private readonly Func<T, bool> isTerminator;
    private readonly Func<T, ReceiveJitterBufferProfile> getProfile;

    public ReceivePacketJitterBuffer(
        Func<T, uint> getStreamId,
        Func<T, ushort> getSequence,
        Func<T, bool> isTerminator,
        Func<T, ReceiveJitterBufferProfile> getProfile)
    {
        this.getStreamId = getStreamId ?? throw new ArgumentNullException(nameof(getStreamId));
        this.getSequence = getSequence ?? throw new ArgumentNullException(nameof(getSequence));
        this.isTerminator = isTerminator ?? throw new ArgumentNullException(nameof(isTerminator));
        this.getProfile = getProfile ?? throw new ArgumentNullException(nameof(getProfile));
    }

    public int Count => packets.Count;

    public void Enqueue(T item, long timestamp)
    {
        uint streamId = getStreamId(item);
        ReceiveJitterBufferProfile profile = getProfile(item);
        if (!streams.TryGetValue(streamId, out StreamState? state))
        {
            state = new StreamState(profile);
            streams.Add(streamId, state);
        }

        bool terminator = isTerminator(item);
        if (!terminator && !state.HasExpectedSequence)
        {
            state.ExpectedSequence = getSequence(item);
            state.HasExpectedSequence = true;
            state.NextDeadline = Add(timestamp, profile.TargetDelay);
        }
        if (terminator)
            state.FlushRequested = true;

        packets.AddLast(new BufferedPacket(item));
    }

    public bool TryDequeue(
        long timestamp,
        bool drain,
        out T item,
        out TimeSpan waitTime,
        out ReceiveJitterBufferDequeueMetadata metadata)
    {
        LinkedListNode<BufferedPacket>? ready = null;
        long earliestDeadline = long.MaxValue;

        foreach ((uint streamId, StreamState state) in streams)
        {
            StreamSelection selection = SelectForStream(streamId, state, timestamp, drain);
            if (selection.Node is not null && selection.IsReady)
            {
                ready = selection.Node;
                break;
            }
            if (selection.Deadline < earliestDeadline)
                earliestDeadline = selection.Deadline;
        }

        if (ready is null)
        {
            item = default!;
            metadata = default;
            waitTime = earliestDeadline == long.MaxValue
                ? Timeout.InfiniteTimeSpan
                : Remaining(timestamp, earliestDeadline);
            return false;
        }

        BufferedPacket selected = ready.Value;
        bool reordered = !isTerminator(selected.Item) &&
            HasEarlierVoicePacketForSameStream(ready);
        StreamState selectedState = streams[getStreamId(selected.Item)];
        int missingPackets = isTerminator(selected.Item) || !selectedState.HasExpectedSequence
            ? 0
            : ForwardDistance(selectedState.ExpectedSequence, getSequence(selected.Item));
        if (missingPackets > MaximumForwardDistance)
            missingPackets = 0;
        packets.Remove(ready);
        AdvanceStream(selected.Item, timestamp);
        item = selected.Item;
        waitTime = TimeSpan.Zero;
        metadata = new ReceiveJitterBufferDequeueMetadata(
            reordered,
            missingPackets,
            selectedState.Profile.TargetDelay,
            selectedState.Profile.IsAdaptive);
        return true;
    }

    private bool HasEarlierVoicePacketForSameStream(LinkedListNode<BufferedPacket> selected)
    {
        uint streamId = getStreamId(selected.Value.Item);
        for (LinkedListNode<BufferedPacket>? node = selected.Previous;
             node is not null;
             node = node.Previous)
        {
            if (!isTerminator(node.Value.Item) && getStreamId(node.Value.Item) == streamId)
                return true;
        }
        return false;
    }

    public bool TryRemoveOldestSuperseded()
    {
        for (LinkedListNode<BufferedPacket>? candidate = packets.First;
             candidate is not null;
             candidate = candidate.Next)
        {
            if (isTerminator(candidate.Value.Item))
                continue;

            uint streamId = getStreamId(candidate.Value.Item);
            for (LinkedListNode<BufferedPacket>? later = candidate.Next;
                 later is not null;
                 later = later.Next)
            {
                if (!isTerminator(later.Value.Item) && getStreamId(later.Value.Item) == streamId)
                {
                    packets.Remove(candidate);
                    return true;
                }
            }
        }
        return false;
    }

    public bool TryRemoveOldest(Predicate<T> predicate)
    {
        for (LinkedListNode<BufferedPacket>? node = packets.First;
             node is not null;
             node = node.Next)
        {
            if (!predicate(node.Value.Item))
                continue;
            T removed = node.Value.Item;
            packets.Remove(node);
            ReconcileTerminatorState(getStreamId(removed));
            return true;
        }
        return false;
    }

    private StreamSelection SelectForStream(
        uint streamId,
        StreamState state,
        long timestamp,
        bool drain)
    {
        LinkedListNode<BufferedPacket>? exact = null;
        LinkedListNode<BufferedPacket>? nearestFuture = null;
        LinkedListNode<BufferedPacket>? late = null;
        LinkedListNode<BufferedPacket>? terminator = null;
        int nearestDistance = int.MaxValue;

        for (LinkedListNode<BufferedPacket>? node = packets.First;
             node is not null;
             node = node.Next)
        {
            T candidate = node.Value.Item;
            if (getStreamId(candidate) != streamId)
                continue;
            if (isTerminator(candidate))
            {
                terminator ??= node;
                continue;
            }

            if (!state.HasExpectedSequence)
                return new StreamSelection(node, true, timestamp);

            int distance = ForwardDistance(state.ExpectedSequence, getSequence(candidate));
            if (distance == 0)
                exact ??= node;
            else if (distance <= MaximumForwardDistance && distance < nearestDistance)
            {
                nearestFuture = node;
                nearestDistance = distance;
            }
            else if (distance > MaximumForwardDistance)
                late ??= node;
        }

        if (late is not null)
            return new StreamSelection(late, true, timestamp);

        bool releaseNow = drain || state.FlushRequested || timestamp >= state.NextDeadline;
        LinkedListNode<BufferedPacket>? voice = exact ?? nearestFuture;
        if (voice is not null)
            return new StreamSelection(voice, releaseNow, state.NextDeadline);

        if (terminator is not null)
            return new StreamSelection(terminator, true, timestamp);

        return default;
    }

    private void AdvanceStream(T item, long timestamp)
    {
        uint streamId = getStreamId(item);
        if (!streams.TryGetValue(streamId, out StreamState? state))
            return;

        if (isTerminator(item))
        {
            streams.Remove(streamId);
            return;
        }

        ushort sequence = getSequence(item);
        int distance = state.HasExpectedSequence
            ? ForwardDistance(state.ExpectedSequence, sequence)
            : 0;
        if (!state.HasExpectedSequence || distance <= MaximumForwardDistance)
        {
            state.ExpectedSequence = NextSequence(sequence);
            state.HasExpectedSequence = true;
            long intervals = Math.Max(1, distance + 1L);
            long next = Add(state.NextDeadline, Multiply(state.Profile.PacketDuration, intervals));
            state.NextDeadline = next > 0 ? next : timestamp;
        }

        ReconcileTerminatorState(streamId);
    }

    private void ReconcileTerminatorState(uint streamId)
    {
        if (!streams.TryGetValue(streamId, out StreamState? state))
            return;
        state.FlushRequested = packets.Any(packet =>
            getStreamId(packet.Item) == streamId && isTerminator(packet.Item));
        if (!packets.Any(packet => getStreamId(packet.Item) == streamId))
            streams.Remove(streamId);
    }

    private static int ForwardDistance(ushort expected, ushort actual)
        => (actual - expected + SequenceModulus) % SequenceModulus;

    private static ushort NextSequence(ushort sequence)
        => sequence == ushort.MaxValue - 1 ? (ushort)0 : (ushort)(sequence + 1);

    private static long Add(long timestamp, TimeSpan duration)
        => timestamp + (long)Math.Round(duration.TotalSeconds * Stopwatch.Frequency);

    private static TimeSpan Multiply(TimeSpan duration, long multiplier)
        => TimeSpan.FromTicks(checked(duration.Ticks * multiplier));

    private static TimeSpan Remaining(long now, long deadline)
        => deadline <= now
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds((deadline - now) / (double)Stopwatch.Frequency);

    private sealed class StreamState(ReceiveJitterBufferProfile profile)
    {
        public ReceiveJitterBufferProfile Profile { get; } = profile;
        public bool HasExpectedSequence { get; set; }
        public ushort ExpectedSequence { get; set; }
        public long NextDeadline { get; set; }
        public bool FlushRequested { get; set; }
    }

    private readonly record struct BufferedPacket(T Item);
    private readonly record struct StreamSelection(
        LinkedListNode<BufferedPacket>? Node,
        bool IsReady,
        long Deadline);
}
