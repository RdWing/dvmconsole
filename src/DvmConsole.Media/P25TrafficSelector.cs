using DvmConsole.Core.Runtime;

namespace DvmConsole.Media;

// Selects one P25 talkgroup for receive playback. The selector deliberately
// accepts only complete voice LDUs; TSDU/PDU/terminator traffic is handled by
// higher-level call-state code.
public sealed record P25TrafficSelector
{
    public P25TrafficSelector(uint destinationId)
    {
        if (destinationId == 0)
            throw new ArgumentOutOfRangeException(nameof(destinationId), "A P25 destination ID must be non-zero.");
        DestinationId = destinationId;
    }

    public uint DestinationId { get; }

    public bool Matches(IRadioMediaFrame traffic)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        return traffic.Protocol == RadioMediaProtocol.P25 &&
            traffic.DestinationId == DestinationId &&
            string.Equals(traffic.FrameType, "VOICE", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(traffic.Subtype, "LDU1", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(traffic.Subtype, "LDU2", StringComparison.OrdinalIgnoreCase));
    }
}
