namespace DvmConsole.Media;

// Filters duplicate and late voice packets while accounting for 16-bit RTP
// sequence wrap. A new stream starts a fresh sequence, so a reused packet
// sequence from a later call cannot be mistaken for a duplicate.
public sealed class VoicePacketSequenceTracker
{
    private bool hasPacket;
    private uint streamId;
    private ushort lastSequence;

    public long LostPackets { get; private set; }
    public long DuplicateOrLatePackets { get; private set; }

    public bool TryAccept(uint packetStreamId, ushort packetSequence)
    {
        if (!hasPacket || streamId != packetStreamId)
        {
            hasPacket = true;
            streamId = packetStreamId;
            lastSequence = packetSequence;
            return true;
        }

        ushort distance = (ushort)(packetSequence - lastSequence);
        if (distance == 0 || distance >= 0x8000)
        {
            DuplicateOrLatePackets++;
            return false;
        }

        long lost = distance - 1;
        // DMR/P25 transmitters reserve 0xFFFF for call-end and wrap from
        // 0xFFFE directly to zero. Do not count that sentinel as lost media.
        if (packetSequence < lastSequence && lastSequence >= ushort.MaxValue - 1)
            lost = Math.Max(0, lost - 1);
        LostPackets += lost;
        lastSequence = packetSequence;
        return true;
    }
}
