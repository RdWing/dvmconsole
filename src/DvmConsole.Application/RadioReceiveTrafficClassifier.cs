using DvmConsole.Core.Runtime;
using DvmConsole.Media;

namespace DvmConsole.Application;

/// <summary>
/// Classifies protocol-neutral radio frames at the application boundary so
/// receive lifecycles do not depend on a concrete network client.
/// </summary>
public static class RadioReceiveTrafficClassifier
{
    public static bool IsTerminator(IRadioMediaFrame traffic)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        if (traffic.FrameType.Equals("TERMINATOR", StringComparison.OrdinalIgnoreCase))
            return true;

        return traffic.Protocol switch
        {
            RadioMediaProtocol.Dmr => traffic.Subtype.Equals(
                "TERMINATOR_WITH_LC",
                StringComparison.OrdinalIgnoreCase),
            RadioMediaProtocol.P25 =>
                traffic.Subtype.Equals("TDU", StringComparison.OrdinalIgnoreCase) ||
                traffic.Subtype.Equals("TDULC", StringComparison.OrdinalIgnoreCase),
            RadioMediaProtocol.Analog => traffic.Subtype.Equals(
                "TERMINATOR",
                StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    public static bool IsDefinitiveStart(IRadioMediaFrame traffic)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        return IsDmrVoiceCallStart(traffic) || IsP25VoiceCallStart(traffic);
    }

    public static bool IsDmrPrivacyHeader(IRadioMediaFrame traffic)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        return traffic.Protocol == RadioMediaProtocol.Dmr &&
               traffic.FrameType.Equals("DATA_SYNC", StringComparison.OrdinalIgnoreCase) &&
               traffic.Subtype.Equals("VOICE_PI_HEADER", StringComparison.OrdinalIgnoreCase);
    }

    public static bool CarriesVoicePayload(IRadioMediaFrame traffic)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        if (!IsVoiceFrame(traffic.FrameType))
            return false;

        return traffic.Protocol switch
        {
            RadioMediaProtocol.P25 =>
                traffic.Subtype.Equals("LDU1", StringComparison.OrdinalIgnoreCase) ||
                traffic.Subtype.Equals("LDU2", StringComparison.OrdinalIgnoreCase),
            RadioMediaProtocol.Dmr or
            RadioMediaProtocol.Nxdn or
            RadioMediaProtocol.Analog => true,
            _ => false
        };
    }

    public static bool IsVoiceFrame(string frameType)
        => frameType.Equals("VOICE", StringComparison.OrdinalIgnoreCase) ||
           frameType.Equals("VOICE_SYNC", StringComparison.OrdinalIgnoreCase);

    internal static ReceiveJitterPacketKind GetJitterPacketKind(IRadioMediaFrame traffic)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        if (IsTerminator(traffic))
            return ReceiveJitterPacketKind.Terminator;
        if (CarriesEncodedVoicePayload(traffic))
            return ReceiveJitterPacketKind.Voice;
        if (IsDefinitiveStart(traffic) ||
            IsDmrPrivacyHeader(traffic) ||
            (traffic.Protocol == RadioMediaProtocol.Nxdn &&
             NxdnVoicePacketCodec.TryExtractCallMetadata(traffic.Payload, out _)))
        {
            return ReceiveJitterPacketKind.Metadata;
        }
        return IsVoiceFrame(traffic.FrameType)
            ? ReceiveJitterPacketKind.Voice
            : ReceiveJitterPacketKind.Metadata;
    }

    public static bool CarriesEncodedVoicePayload(IRadioMediaFrame traffic)
        => CarriesVoicePayload(traffic) &&
           (traffic.Protocol != RadioMediaProtocol.Nxdn ||
            !NxdnVoicePacketCodec.TryExtractCallMetadata(traffic.Payload, out _));

    private static bool IsDmrVoiceCallStart(IRadioMediaFrame traffic)
        => traffic.Protocol == RadioMediaProtocol.Dmr &&
           traffic.FrameType.Equals("DATA_SYNC", StringComparison.OrdinalIgnoreCase) &&
           traffic.Subtype.Equals("VOICE_LC_HEADER", StringComparison.OrdinalIgnoreCase);

    private static bool IsP25VoiceCallStart(IRadioMediaFrame traffic)
        => traffic.Protocol == RadioMediaProtocol.P25 &&
           traffic.PacketSequence == 0 &&
           traffic.FrameType.Equals("VOICE", StringComparison.OrdinalIgnoreCase) &&
           traffic.Subtype.Equals("LDU1", StringComparison.OrdinalIgnoreCase);
}
