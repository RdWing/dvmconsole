using DvmConsole.Application;

namespace DvmConsole.Desktop;

internal static class DesktopTransmitChannelAdapter
{
    public static TransmitChannelDescriptor ToTransmitDescriptor(this ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return new TransmitChannelDescriptor(
            new ChannelId(channel.SessionId),
            channel.Definition,
            channel.IsReceivePresentationActive,
            channel.IsTransmitEncrypted,
            channel.CanTransmitByConfiguration,
            channel.ConfigurationTransmitUnavailableReason,
            channel.TalkgroupUnavailableReason,
            channel.HasCallPriority);
    }

    public static Task StartAsync(
        this ChannelTransmitCoordinator coordinator,
        ChannelViewModel channel,
        IFneTrafficEndpoint system)
        => coordinator.StartAsync(channel.ToTransmitDescriptor(), system);

    public static uint GetActiveStreamId(
        this ChannelTransmitCoordinator coordinator,
        ChannelViewModel channel)
        => coordinator.GetActiveStreamId(new ChannelId(channel.SessionId));

    public static Task SendAsync(
        this ToneTransmitCoordinator coordinator,
        ChannelViewModel channel,
        IFneTrafficEndpoint system,
        ReadOnlyMemory<short> samples,
        CancellationToken cancellationToken = default)
        => coordinator.SendAsync(
            channel.ToTransmitDescriptor(),
            system,
            samples,
            cancellationToken);
}
