using DvmConsole.Application;
namespace DvmConsole.Application;

// Centralizes the live FNE policy used by every outbound audio path. The
// channel view model retains a presentation snapshot, but safety decisions are
// made against the endpoint's current authority at the instant a call starts.
internal static class TransmitTargetPolicy
{
    public static TargetAuthorityState GetTalkgroupAvailability(
        TransmitChannelDescriptor channel,
        IRadioTrafficEndpoint system)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(system);
        return system.GetTargetAuthority(
            ChannelProtocolMediaMapper.ToTrafficProtocol(channel.Definition.Protocol),
            channel.Definition.DestinationId,
            channel.Definition.Slot);
    }

    public static bool IsAvailable(TransmitChannelDescriptor channel, IRadioTrafficEndpoint system)
        => channel.CanTransmitByConfiguration &&
            GetTalkgroupAvailability(channel, system) != TargetAuthorityState.Unavailable;

    public static void ThrowIfUnavailable(TransmitChannelDescriptor channel, IRadioTrafficEndpoint system)
    {
        TargetAuthorityState availability = GetTalkgroupAvailability(channel, system);
        if (availability == TargetAuthorityState.Unavailable)
            throw new InvalidOperationException(
                $"{channel.Name} cannot transmit because {channel.AuthorityUnavailableReason}.");
        if (!channel.CanTransmitByConfiguration)
            throw new InvalidOperationException(
                $"{channel.Name} cannot transmit because {channel.ConfigurationUnavailableReason}.");
    }
}
