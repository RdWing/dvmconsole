using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

// Centralizes the live FNE policy used by every outbound audio path. The
// channel view model retains a presentation snapshot, but safety decisions are
// made against the endpoint's current authority at the instant a call starts.
internal static class TransmitTargetPolicy
{
    public static FneTalkgroupAvailability GetTalkgroupAvailability(
        ChannelViewModel channel,
        IFneTrafficEndpoint system)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(system);
        return system.GetTalkgroupAvailability(
            FneTrafficProtocolMapper.FromChannelProtocol(channel.Definition.Protocol),
            channel.Definition.DestinationId,
            channel.Definition.Slot);
    }

    public static bool IsAvailable(ChannelViewModel channel, IFneTrafficEndpoint system)
        => channel.CanTransmitByConfiguration &&
            GetTalkgroupAvailability(channel, system) != FneTalkgroupAvailability.Unavailable;

    public static void ThrowIfUnavailable(ChannelViewModel channel, IFneTrafficEndpoint system)
    {
        FneTalkgroupAvailability availability = GetTalkgroupAvailability(channel, system);
        if (availability == FneTalkgroupAvailability.Unavailable)
            throw new InvalidOperationException(
                $"{channel.Name} cannot transmit because {channel.TalkgroupUnavailableReason}.");
        if (!channel.CanTransmitByConfiguration)
            throw new InvalidOperationException(
                $"{channel.Name} cannot transmit because {channel.ConfigurationTransmitUnavailableReason}.");
    }
}
