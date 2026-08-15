using DvmConsole.FneClient;

namespace DvmConsole.Media;

/// <summary>
/// Selects voice frames for one configured analog talkgroup.
/// </summary>
public sealed record AnalogTrafficSelector
{
    public AnalogTrafficSelector(uint destinationId)
    {
        if (destinationId == 0)
            throw new ArgumentOutOfRangeException(nameof(destinationId), "An analog destination ID must be non-zero.");
        DestinationId = destinationId;
    }

    public uint DestinationId { get; }

    public bool Matches(FneTrafficFrame traffic)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        return traffic.Protocol == FneTrafficProtocol.Analog &&
            traffic.DestinationId == DestinationId &&
            string.Equals(traffic.FrameType, "VOICE", StringComparison.OrdinalIgnoreCase);
    }
}
