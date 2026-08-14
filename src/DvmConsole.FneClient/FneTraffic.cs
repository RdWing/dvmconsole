namespace DvmConsole.FneClient;

public enum FneTrafficProtocol
{
    Dmr,
    P25,
    Nxdn,
    Analog
}

/// <summary>
/// Platform-neutral representation of an inbound FNE media frame. Enum values
/// from the legacy protocol library are intentionally represented as strings so
/// the desktop/audio layers do not depend on fnecore implementation types.
/// </summary>
public sealed record FneTrafficFrame
{
    public FneTrafficFrame(
        FneTrafficProtocol protocol,
        uint peerId,
        uint sourceId,
        uint destinationId,
        byte? slot,
        string callType,
        string frameType,
        string subtype,
        ushort packetSequence,
        uint streamId,
        ReadOnlySpan<byte> payload)
    {
        Protocol = protocol;
        PeerId = peerId;
        SourceId = sourceId;
        DestinationId = destinationId;
        Slot = slot;
        CallType = callType;
        FrameType = frameType;
        Subtype = subtype;
        PacketSequence = packetSequence;
        StreamId = streamId;
        Payload = payload.ToArray();
    }

    public FneTrafficProtocol Protocol { get; }
    public uint PeerId { get; }
    public uint SourceId { get; }
    public uint DestinationId { get; }
    public byte? Slot { get; }
    public string CallType { get; }
    public string FrameType { get; }
    public string Subtype { get; }
    public ushort PacketSequence { get; }
    public uint StreamId { get; }
    public byte[] Payload { get; }
}
