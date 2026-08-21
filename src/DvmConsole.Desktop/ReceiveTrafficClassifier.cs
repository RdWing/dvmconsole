using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

// Centralizes receive-frame lifecycle classification so UI state, priority
// routing, decoder ordering, and audio cleanup cannot drift independently.
internal static class ReceiveTrafficClassifier
{
    public static bool IsTerminator(FneTrafficFrame traffic)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        if (traffic.FrameType.Equals("TERMINATOR", StringComparison.OrdinalIgnoreCase))
            return true;

        return traffic.Protocol switch
        {
            FneTrafficProtocol.Dmr => traffic.Subtype.Equals(
                "TERMINATOR_WITH_LC",
                StringComparison.OrdinalIgnoreCase),
            FneTrafficProtocol.P25 => traffic.Subtype.Equals("TDU", StringComparison.OrdinalIgnoreCase) ||
                                      traffic.Subtype.Equals("TDULC", StringComparison.OrdinalIgnoreCase),
            FneTrafficProtocol.Analog => traffic.Subtype.Equals(
                "TERMINATOR",
                StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    public static bool IsDefinitiveStart(FneTrafficFrame traffic)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        return traffic.Protocol == FneTrafficProtocol.Dmr &&
               traffic.FrameType.Equals("DATA_SYNC", StringComparison.OrdinalIgnoreCase) &&
               traffic.Subtype.Equals("VOICE_LC_HEADER", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDmrPrivacyHeader(FneTrafficFrame traffic)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        return traffic.Protocol == FneTrafficProtocol.Dmr &&
               traffic.FrameType.Equals("DATA_SYNC", StringComparison.OrdinalIgnoreCase) &&
               traffic.Subtype.Equals("VOICE_PI_HEADER", StringComparison.OrdinalIgnoreCase);
    }

    public static bool CarriesVoicePayload(FneTrafficFrame traffic)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        if (!IsVoiceFrame(traffic.FrameType))
            return false;

        return traffic.Protocol switch
        {
            FneTrafficProtocol.P25 =>
                traffic.Subtype.Equals("LDU1", StringComparison.OrdinalIgnoreCase) ||
                traffic.Subtype.Equals("LDU2", StringComparison.OrdinalIgnoreCase),
            FneTrafficProtocol.Dmr or
            FneTrafficProtocol.Nxdn or
            FneTrafficProtocol.Analog => true,
            _ => false
        };
    }

    public static bool IsVoiceFrame(string frameType)
        => frameType.Equals("VOICE", StringComparison.OrdinalIgnoreCase) ||
           frameType.Equals("VOICE_SYNC", StringComparison.OrdinalIgnoreCase);
}
