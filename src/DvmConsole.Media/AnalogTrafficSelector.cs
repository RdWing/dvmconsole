using DvmConsole.Core.Runtime;

namespace DvmConsole.Media;

// Selects voice frames for one configured analog talkgroup.
public sealed record AnalogTrafficSelector
{
    public AnalogTrafficSelector(uint destinationId)
    {
        if (destinationId == 0)
            throw new ArgumentOutOfRangeException(nameof(destinationId), "An analog destination ID must be non-zero.");
        DestinationId = destinationId;
    }

    public uint DestinationId { get; }

    public bool Matches(IRadioMediaFrame traffic)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        return traffic.Protocol == RadioMediaProtocol.Analog &&
            traffic.DestinationId == DestinationId &&
            string.Equals(traffic.FrameType, "VOICE", StringComparison.OrdinalIgnoreCase);
    }
}
