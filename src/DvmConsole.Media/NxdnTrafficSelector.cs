using DvmConsole.Core.Runtime;

namespace DvmConsole.Media;

// Selects voice and call-control traffic for one NXDN destination.
public sealed record NxdnTrafficSelector
{
    public NxdnTrafficSelector(uint destinationId)
    {
        if (destinationId == 0)
            throw new ArgumentOutOfRangeException(nameof(destinationId), "An NXDN destination ID must be non-zero.");
        DestinationId = destinationId;
    }

    public uint DestinationId { get; }

    public bool Matches(IRadioMediaFrame traffic)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        return traffic.Protocol == RadioMediaProtocol.Nxdn &&
            traffic.DestinationId == DestinationId &&
            (string.Equals(traffic.FrameType, "VOICE", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(traffic.FrameType, "TERMINATOR", StringComparison.OrdinalIgnoreCase));
    }
}
