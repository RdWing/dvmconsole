using DvmConsole.FneClient;

namespace DvmConsole.Media;

// Selects the DMR voice stream that belongs to one configured talkgroup and
// timeslot. FNE reports DMR slots as zero-based values; codeplug conversion
// belongs at the configuration boundary.
public sealed record DmrTrafficSelector
{
    public DmrTrafficSelector(uint destinationId, byte slot)
    {
        if (destinationId == 0)
            throw new ArgumentOutOfRangeException(nameof(destinationId), "A DMR destination ID must be non-zero.");
        if (slot > 1)
            throw new ArgumentOutOfRangeException(nameof(slot), "A DMR slot must be zero or one.");

        DestinationId = destinationId;
        Slot = slot;
    }

    public uint DestinationId { get; }
    public byte Slot { get; }

    public bool Matches(FneTrafficFrame traffic)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        return traffic.Protocol == FneTrafficProtocol.Dmr &&
            traffic.DestinationId == DestinationId &&
            traffic.Slot == Slot &&
            (IsVoiceFrame(traffic.FrameType) ||
             DmrVoicePacketCodec.IsPrivacyIndicator(traffic.Payload));
    }

    private static bool IsVoiceFrame(string? frameType)
    {
        return string.Equals(frameType, "VOICE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(frameType, "VOICE_SYNC", StringComparison.OrdinalIgnoreCase);
    }
}
