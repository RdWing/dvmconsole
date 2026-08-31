using DvmConsole.Core.Runtime;
using DvmConsole.Vocoder;

namespace DvmConsole.Application;

internal static class ChannelVocoderPolicy
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
}
