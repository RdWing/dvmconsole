using System.Diagnostics;

namespace DvmConsole.FneClient;

public enum FneTrafficProtocol
{
    Dmr,
    P25,
    Nxdn,
    Analog
}

// Platform-neutral representation of an inbound FNE media frame. Enum values
// from the legacy protocol library are intentionally represented as strings so
// the desktop/audio layers do not depend on fnecore implementation types.
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
        ReadOnlySpan<byte> payload,
        long fneBoundaryTimestamp = 0)
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
        FneBoundaryTimestamp = fneBoundaryTimestamp > 0
            ? fneBoundaryTimestamp
            : Stopwatch.GetTimestamp();
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
    // Monotonic timestamp taken at the app-owned fnecore event boundary. It
    // bounds delay above fnecore without depending on wall-clock adjustments.
    public long FneBoundaryTimestamp { get; }
}
