using DvmConsole.FneClient;
using DvmConsole.Media;

namespace DvmConsole.Desktop;

internal readonly record struct SystemTrafficWorkItem(
    FneTrafficFrame Traffic,
    DateTimeOffset ReceivedAt,
    long ReceivedTimestamp,
    IReadOnlyList<ChannelViewModel> PreEnqueuedAudioChannels);

// Bounds media waiting for the UI-thread routing pass. Lifecycle frames are
// retained preferentially so dropping stale voice cannot strand a call active.
internal sealed class SystemTrafficBuffer
{
    private readonly LinkedList<SystemTrafficWorkItem> pending = [];
    private readonly int maximumCount;

    public SystemTrafficBuffer(int maximumCount = 256)
    {
        if (maximumCount < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        this.maximumCount = maximumCount;
    }

    public int Count => pending.Count;
    public long DroppedCount { get; private set; }

    public bool Enqueue(FneTrafficFrame traffic)
        => Enqueue(new SystemTrafficWorkItem(
            traffic,
            DateTimeOffset.UtcNow,
            0,
            []));

    public bool Enqueue(SystemTrafficWorkItem item)
    {
        FneTrafficFrame traffic = item.Traffic;
        if (pending.Count >= maximumCount)
        {
            DroppedCount++;
            if (!MakeRoomFor(traffic))
                return false;
        }
        pending.AddLast(item);
        return true;
    }

    public bool TryDequeue(out FneTrafficFrame? traffic)
    {
        bool found = TryDequeue(out SystemTrafficWorkItem? item);
        traffic = item?.Traffic;
        return found;
    }

    public bool TryDequeue(out SystemTrafficWorkItem? item)
    {
        if (pending.First is null)
        {
            item = null;
            return false;
        }

        item = pending.First.Value;
        pending.RemoveFirst();
        return true;
    }

    private bool MakeRoomFor(FneTrafficFrame incoming)
    {
        LinkedListNode<SystemTrafficWorkItem>? candidate = pending.First;
        while (candidate is not null)
        {
            if (!IsLifecycleTraffic(candidate.Value.Traffic) &&
                HasLaterVoiceForSameStream(candidate))
                break;
            candidate = candidate.Next;
        }

        if (candidate is null && ReceiveTrafficClassifier.IsTerminator(incoming))
        {
            candidate = pending.First;
            while (candidate is not null &&
                   (IsLifecycleTraffic(candidate.Value.Traffic) ||
                    candidate.Value.Traffic.StreamId == incoming.StreamId))
            {
                candidate = candidate.Next;
            }
        }

        candidate ??= pending.First;
        while (candidate is not null && IsLifecycleTraffic(candidate.Value.Traffic))
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

    private static bool HasLaterVoiceForSameStream(
        LinkedListNode<SystemTrafficWorkItem> candidate)
    {
        for (LinkedListNode<SystemTrafficWorkItem>? later = candidate.Next;
             later is not null;
             later = later.Next)
        {
            if (!IsLifecycleTraffic(later.Value.Traffic) &&
                later.Value.Traffic.StreamId == candidate.Value.Traffic.StreamId)
                return true;
        }
        return false;
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

        return ReceiveTrafficClassifier.IsTerminator(traffic) ||
               ReceiveTrafficClassifier.IsDefinitiveStart(traffic) ||
               ReceiveTrafficClassifier.IsDmrPrivacyHeader(traffic);
    }
}
