using DvmConsole.FneClient;

namespace DvmConsole.Media;

// Selects one NXDN destination for an injected NXDN decoder. Message framing
// and codec interpretation remain separate from destination selection.
public sealed record NxdnTrafficSelector
{
    public NxdnTrafficSelector(uint destinationId)
    {
        if (destinationId == 0)
            throw new ArgumentOutOfRangeException(nameof(destinationId), "An NXDN destination ID must be non-zero.");
        DestinationId = destinationId;
    }

    public uint DestinationId { get; }

    public bool Matches(FneTrafficFrame traffic)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        return traffic.Protocol == FneTrafficProtocol.Nxdn &&
            traffic.DestinationId == DestinationId &&
            string.Equals(traffic.FrameType, "VOICE", StringComparison.OrdinalIgnoreCase);
    }
}
