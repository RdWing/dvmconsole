namespace DvmConsole.Core.Runtime;

public enum RadioMediaProtocol
{
    Dmr,
    P25,
    Nxdn,
    Analog
}

// Protocol-library adapters implement this immutable media descriptor at the
// ingress boundary. Media processing therefore does not depend on a concrete
// network client or its runtime types.
public interface IRadioMediaFrame
{
    RadioMediaProtocol Protocol { get; }
    uint PeerId { get; }
    uint SourceId { get; }
    uint DestinationId { get; }
    byte? Slot { get; }
    string CallType { get; }
    string FrameType { get; }
    string Subtype { get; }
    ushort PacketSequence { get; }
    uint StreamId { get; }
    byte[] Payload { get; }
}
