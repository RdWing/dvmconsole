using DvmConsole.Core.Runtime;
using DvmConsole.Vocoder;

namespace DvmConsole.Application;

// Centralizes the media capabilities associated with one normalized channel
// protocol. Callers should not reinterpret the persisted Mode string.
internal static class ChannelProtocolMediaMapper
{
    public static bool RequiresVocoder(ChannelProtocol protocol)
        => protocol != ChannelProtocol.Analog;

    public static VocoderMode ToVocoderMode(ChannelProtocol protocol)
        => protocol switch
        {
            ChannelProtocol.Dmr => VocoderMode.DmrAmbe,
            ChannelProtocol.P25 => VocoderMode.P25Imbe,
            ChannelProtocol.Nxdn => VocoderMode.NxdnAmbe,
            ChannelProtocol.Analog => throw new ArgumentException(
                "Analog channels do not use a vocoder session.",
                nameof(protocol)),
            _ => throw new ArgumentOutOfRangeException(nameof(protocol))
        };

    public static RadioMediaProtocol ToTrafficProtocol(ChannelProtocol protocol)
        => protocol switch
        {
            ChannelProtocol.Dmr => RadioMediaProtocol.Dmr,
            ChannelProtocol.P25 => RadioMediaProtocol.P25,
            ChannelProtocol.Nxdn => RadioMediaProtocol.Nxdn,
            ChannelProtocol.Analog => RadioMediaProtocol.Analog,
            _ => throw new ArgumentOutOfRangeException(nameof(protocol))
        };
}
