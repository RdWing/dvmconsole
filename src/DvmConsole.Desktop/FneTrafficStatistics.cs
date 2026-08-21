using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

// Retains stable connection totals and only a bounded set of active RX stream
// counters. Completed streams are reduced to one immutable last-stream summary.
internal sealed class FneTrafficStatistics
{
    private const int MaximumTrackedStreams = 32;
    private readonly object sync = new();
    private readonly Dictionary<uint, StreamCounter> activeReceiveStreams = [];
    private readonly LinkedList<uint> receiveStreamOrder = [];
    private long receivedBytes;
    private long sentBytes;
    private FneStreamTrafficSnapshot? latestReceiveStream;

    public string TotalsText
    {
        get
        {
            lock (sync)
                return $"Media this connection · RX {FormatBytes(receivedBytes)} · TX {FormatBytes(sentBytes)}";
        }
    }

    public string StreamText
    {
        get
        {
            lock (sync)
            {
                if (latestReceiveStream is not FneStreamTrafficSnapshot stream)
                    return "No RX media stream in this connection session.";

                string state = stream.Completed ? "Last RX stream" : "Current RX stream";
                string ended = stream.Completed ? " · ended" : string.Empty;
                return $"{state} · {stream.Protocol.ToString().ToUpperInvariant()} " +
                    $"{stream.CallType}/{stream.FrameType} · {stream.PacketCount:N0} packets / " +
                    $"{FormatBytes(stream.PayloadBytes)} · stream {stream.StreamId} · " +
                    $"{stream.SourceId}→{stream.DestinationId}{ended}";
            }
        }
    }

    public void ObserveReceive(FneTrafficFrame traffic)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        lock (sync)
        {
            receivedBytes = SaturatingAdd(receivedBytes, traffic.Payload.Length);
            if (!activeReceiveStreams.TryGetValue(traffic.StreamId, out StreamCounter? stream))
            {
                LinkedListNode<uint> orderNode = receiveStreamOrder.AddLast(traffic.StreamId);
                stream = new StreamCounter(traffic, orderNode);
                activeReceiveStreams.Add(traffic.StreamId, stream);
                TrimActiveStreams();
            }

            stream.Observe(traffic);
            bool completed = ReceiveTrafficClassifier.IsTerminator(traffic);
            latestReceiveStream = stream.Snapshot(completed);
            if (completed)
            {
                activeReceiveStreams.Remove(traffic.StreamId);
                receiveStreamOrder.Remove(stream.OrderNode);
            }
        }
    }

    public void ObserveSend(int payloadBytes)
    {
        if (payloadBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(payloadBytes));
        lock (sync)
            sentBytes = SaturatingAdd(sentBytes, payloadBytes);
    }

    public void Reset()
    {
        lock (sync)
        {
            receivedBytes = 0;
            sentBytes = 0;
            activeReceiveStreams.Clear();
            receiveStreamOrder.Clear();
            latestReceiveStream = null;
        }
    }

    internal static string FormatBytes(long bytes)
    {
        if (bytes < 0)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        if (bytes < 1_000)
            return $"{bytes:N0} B";
        if (bytes < 1_000_000)
            return $"{bytes / 1_000d:0.0} KB";
        if (bytes < 1_000_000_000)
            return $"{bytes / 1_000_000d:0.0} MB";
        return $"{bytes / 1_000_000_000d:0.0} GB";
    }

    private void TrimActiveStreams()
    {
        while (activeReceiveStreams.Count > MaximumTrackedStreams && receiveStreamOrder.First is not null)
        {
            uint oldestStreamId = receiveStreamOrder.First.Value;
            receiveStreamOrder.RemoveFirst();
            activeReceiveStreams.Remove(oldestStreamId);
        }
    }

    private static long SaturatingAdd(long current, int increment)
        => current > long.MaxValue - increment ? long.MaxValue : current + increment;

    private sealed class StreamCounter
    {
        private long packetCount;
        private long payloadBytes;
        private FneTrafficFrame latest;

        public StreamCounter(FneTrafficFrame first, LinkedListNode<uint> orderNode)
        {
            latest = first;
            OrderNode = orderNode;
        }

        public LinkedListNode<uint> OrderNode { get; }

        public void Observe(FneTrafficFrame traffic)
        {
            latest = traffic;
            packetCount = packetCount == long.MaxValue ? long.MaxValue : packetCount + 1;
            payloadBytes = SaturatingAdd(payloadBytes, traffic.Payload.Length);
        }

        public FneStreamTrafficSnapshot Snapshot(bool completed)
            => new(
                latest.Protocol,
                latest.CallType,
                latest.FrameType,
                latest.StreamId,
                latest.SourceId,
                latest.DestinationId,
                packetCount,
                payloadBytes,
                completed);
    }
}

internal readonly record struct FneStreamTrafficSnapshot(
    FneTrafficProtocol Protocol,
    string CallType,
    string FrameType,
    uint StreamId,
    uint SourceId,
    uint DestinationId,
    long PacketCount,
    long PayloadBytes,
    bool Completed);
