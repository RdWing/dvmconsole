using DvmConsole.Core.Runtime;
using DvmConsole.Core.Settings;

namespace DvmConsole.Application;

/// <summary>
/// Converts persisted, protocol-specific operator settings into the immutable
/// receive policy snapshotted by each new stream.
/// </summary>
internal static class ReceiveJitterBufferConfigurationPolicy
{
    private static readonly TimeSpan P25PacketDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan DmrPacketDuration = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan NxdnPacketDuration = TimeSpan.FromMilliseconds(80);

    public static ReceiveJitterBufferConfiguration GetConfiguration(
        RadioMediaProtocol protocol,
        RxJitterBufferSetting settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return protocol switch
        {
            RadioMediaProtocol.P25 => CreateConfiguration(
                P25PacketDuration,
                settings.P25Milliseconds,
                RxJitterBufferSetting.MaximumP25Milliseconds,
                settings.P25Adaptive),
            RadioMediaProtocol.Dmr => CreateConfiguration(
                DmrPacketDuration,
                settings.DmrMilliseconds,
                RxJitterBufferSetting.MaximumDmrMilliseconds,
                settings.DmrAdaptive),
            RadioMediaProtocol.Nxdn => CreateConfiguration(
                NxdnPacketDuration,
                settings.NxdnMilliseconds,
                RxJitterBufferSetting.MaximumNxdnMilliseconds,
                settings.NxdnAdaptive),
            _ => default
        };
    }

    private static ReceiveJitterBufferConfiguration CreateConfiguration(
        TimeSpan packetDuration,
        int configuredMilliseconds,
        int adaptiveMaximumMilliseconds,
        bool adaptive)
    {
        TimeSpan configuredDelay = TimeSpan.FromMilliseconds(configuredMilliseconds);
        if (!adaptive)
        {
            return new ReceiveJitterBufferConfiguration(
                packetDuration,
                configuredDelay,
                configuredDelay,
                IsAdaptive: false);
        }

        return new ReceiveJitterBufferConfiguration(
            packetDuration,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(adaptiveMaximumMilliseconds),
            IsAdaptive: true);
    }
}
