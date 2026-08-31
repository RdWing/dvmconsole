namespace DvmConsole.Application;

internal enum ReceiveJitterEventPublicationKind
{
    First,
    Periodic,
    Final
}

internal readonly record struct ReceiveJitterEventPublication(
    ReceiveJitterEventPublicationKind Kind,
    uint StreamId,
    ushort LatestSequence,
    long ReorderedSincePrevious,
    long MissedSincePrevious,
    long TotalReordered,
    long TotalMissed);

// Coalesces packet-level jitter evidence per physical stream. The state is
// bounded so a malformed or never-terminated source cannot grow memory usage.
internal sealed class ReceiveJitterEventReporter
{
    internal const int DefaultMaximumTrackedStreams = 512;
    private readonly object sync = new();
    private readonly Dictionary<StreamKey, StreamState> states = [];
    private readonly LinkedList<StreamKey> order = [];
    private readonly TimeSpan minimumInterval;
    private readonly int maximumTrackedStreams;

    public ReceiveJitterEventReporter(
        TimeSpan minimumInterval,
        int maximumTrackedStreams = DefaultMaximumTrackedStreams)
    {
        if (minimumInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumInterval));
        if (maximumTrackedStreams < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumTrackedStreams));
        this.minimumInterval = minimumInterval;
        this.maximumTrackedStreams = maximumTrackedStreams;
    }

    public ReceiveJitterEventPublication? Observe(
        ChannelId channelId,
        ReceiveWorkItemTiming timing,
        DateTimeOffset now)
    {
        if (!timing.JitterBufferReorderedPacket && timing.JitterBufferDeadlineMissedPackets <= 0)
            return null;

        lock (sync)
        {
            var key = new StreamKey(channelId, timing.Traffic.StreamId);
            StreamState state = GetOrCreate(key);
            state.LatestSequence = timing.Traffic.PacketSequence;
            if (timing.JitterBufferReorderedPacket)
            {
                state.PendingReordered = SaturatingIncrement(state.PendingReordered);
                state.TotalReordered = SaturatingIncrement(state.TotalReordered);
            }
            if (timing.JitterBufferDeadlineMissedPackets > 0)
            {
                state.PendingMissed = SaturatingAdd(
                    state.PendingMissed,
                    timing.JitterBufferDeadlineMissedPackets);
                state.TotalMissed = SaturatingAdd(
                    state.TotalMissed,
                    timing.JitterBufferDeadlineMissedPackets);
            }

            if (state.LastPublishedAt is DateTimeOffset lastPublishedAt &&
                now - lastPublishedAt < minimumInterval)
            {
                return null;
            }

            ReceiveJitterEventPublicationKind kind = state.LastPublishedAt is null
                ? ReceiveJitterEventPublicationKind.First
                : ReceiveJitterEventPublicationKind.Periodic;
            return Publish(state, key.StreamId, kind, now);
        }
    }

    public ReceiveJitterEventPublication? Complete(
        ChannelId channelId,
        uint streamId)
    {
        lock (sync)
        {
            var key = new StreamKey(channelId, streamId);
            if (!states.Remove(key, out StreamState? state))
                return null;
            order.Remove(state.OrderNode);
            return new ReceiveJitterEventPublication(
                ReceiveJitterEventPublicationKind.Final,
                streamId,
                state.LatestSequence,
                state.PendingReordered,
                state.PendingMissed,
                state.TotalReordered,
                state.TotalMissed);
        }
    }

    public void Reset(ChannelId channelId)
    {
        lock (sync)
        {
            LinkedListNode<StreamKey>? node = order.First;
            while (node is not null)
            {
                LinkedListNode<StreamKey>? next = node.Next;
                if (node.Value.ChannelId == channelId)
                {
                    states.Remove(node.Value);
                    order.Remove(node);
                }
                node = next;
            }
        }
    }

    private StreamState GetOrCreate(StreamKey key)
    {
        if (states.TryGetValue(key, out StreamState? state))
            return state;

        while (states.Count >= maximumTrackedStreams && order.First is not null)
        {
            StreamKey oldest = order.First.Value;
            order.RemoveFirst();
            states.Remove(oldest);
        }

        LinkedListNode<StreamKey> node = order.AddLast(key);
        state = new StreamState(node);
        states.Add(key, state);
        return state;
    }

    private static ReceiveJitterEventPublication Publish(
        StreamState state,
        uint streamId,
        ReceiveJitterEventPublicationKind kind,
        DateTimeOffset now)
    {
        var publication = new ReceiveJitterEventPublication(
            kind,
            streamId,
            state.LatestSequence,
            state.PendingReordered,
            state.PendingMissed,
            state.TotalReordered,
            state.TotalMissed);
        state.PendingReordered = 0;
        state.PendingMissed = 0;
        state.LastPublishedAt = now;
        return publication;
    }

    private static long SaturatingIncrement(long value)
        => value == long.MaxValue ? long.MaxValue : value + 1;

    private static long SaturatingAdd(long left, long right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;

    private readonly record struct StreamKey(ChannelId ChannelId, uint StreamId);

    private sealed class StreamState(LinkedListNode<StreamKey> orderNode)
    {
        public LinkedListNode<StreamKey> OrderNode { get; } = orderNode;
        public ushort LatestSequence { get; set; }
        public long PendingReordered { get; set; }
        public long PendingMissed { get; set; }
        public long TotalReordered { get; set; }
        public long TotalMissed { get; set; }
        public DateTimeOffset? LastPublishedAt { get; set; }
    }
}
