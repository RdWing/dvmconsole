using DvmConsole.FneClient;
using DvmConsole.Media;

namespace DvmConsole.Desktop;

// Bounds media waiting for the UI-thread routing pass. Lifecycle frames are
// retained preferentially so dropping stale voice cannot strand a call active.
internal sealed class SystemTrafficBuffer
{
    private readonly LinkedList<FneTrafficFrame> pending = [];
    private readonly int maximumCount;

    public SystemTrafficBuffer(int maximumCount = 256)
    {
        if (maximumCount < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        this.maximumCount = maximumCount;
    }

    public int Count => pending.Count;

    public bool Enqueue(FneTrafficFrame traffic)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        if (pending.Count >= maximumCount && !MakeRoomFor(traffic))
            return false;
        pending.AddLast(traffic);
        return true;
    }

    public bool TryDequeue(out FneTrafficFrame? traffic)
    {
        if (pending.First is null)
        {
            traffic = null;
            return false;
        }

        traffic = pending.First.Value;
        pending.RemoveFirst();
        return true;
    }

    private bool MakeRoomFor(FneTrafficFrame incoming)
    {
        LinkedListNode<FneTrafficFrame>? candidate = pending.First;
        while (candidate is not null && IsLifecycleTraffic(candidate.Value))
            candidate = candidate.Next;

        if (candidate is not null)
        {
            pending.Remove(candidate);
            return true;
        }

        if (!IsLifecycleTraffic(incoming))
            return false;

        pending.RemoveFirst();
        return true;
    }

    private static bool IsLifecycleTraffic(FneTrafficFrame traffic)
    {
        if (TrafficEncryptionMetadataResolver.TryResolve(traffic) is not null)
            return true;

        if (traffic.Protocol == FneTrafficProtocol.Nxdn &&
            NxdnVoicePacketCodec.TryExtractCallMetadata(
                traffic.Payload,
                out NxdnVoicePacketCodec.CallMetadata metadata) &&
            metadata.MessageType is NxdnVoicePacketCodec.VoiceCallMessageType or
                NxdnVoicePacketCodec.VoiceCallIvMessageType or
                NxdnVoicePacketCodec.TransmitReleaseMessageType)
        {
            return true;
        }

        if (traffic.FrameType.Equals("TERMINATOR", StringComparison.OrdinalIgnoreCase))
            return true;
        if (traffic.Protocol == FneTrafficProtocol.Dmr &&
            traffic.FrameType.Equals("DATA_SYNC", StringComparison.OrdinalIgnoreCase) &&
            traffic.Subtype.Equals("VOICE_PI_HEADER", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return traffic.Protocol switch
        {
            FneTrafficProtocol.Dmr => traffic.Subtype.Equals("TERMINATOR_WITH_LC", StringComparison.OrdinalIgnoreCase),
            FneTrafficProtocol.P25 => traffic.Subtype.Equals("TDU", StringComparison.OrdinalIgnoreCase) ||
                                       traffic.Subtype.Equals("TDULC", StringComparison.OrdinalIgnoreCase),
            FneTrafficProtocol.Analog => traffic.Subtype.Equals("TERMINATOR", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }
}
