using DvmConsole.Core.Runtime;
using DvmConsole.FneClient;
using DvmConsole.Vocoder;

namespace DvmConsole.Desktop;

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

    public static FneTrafficProtocol ToTrafficProtocol(ChannelProtocol protocol)
        => FneTrafficProtocolMapper.FromChannelProtocol(protocol);
}
