using DvmConsole.Core.Settings;
using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

internal readonly record struct ReceiveJitterBufferProfile(
    TimeSpan PacketDuration,
    TimeSpan TargetDelay,
    bool IsAdaptive = false)
{
    public bool IsEnabled => TargetDelay > TimeSpan.Zero;
}

internal readonly record struct ReceiveJitterBufferConfiguration(
    TimeSpan PacketDuration,
    TimeSpan InitialDelay,
    TimeSpan MaximumDelay,
    bool IsAdaptive)
{
    public ReceiveJitterBufferProfile CreateProfile(TimeSpan targetDelay)
        => new(PacketDuration, targetDelay, IsAdaptive);
}

internal static class ReceiveJitterBufferPolicy
{
    private static readonly TimeSpan P25PacketDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan DmrPacketDuration = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan NxdnPacketDuration = TimeSpan.FromMilliseconds(80);

    public static ReceiveJitterBufferConfiguration GetConfiguration(
        FneTrafficProtocol protocol,
        RxJitterBufferSetting settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return protocol switch
        {
            FneTrafficProtocol.P25 => CreateConfiguration(
                P25PacketDuration,
                settings.P25Milliseconds,
                RxJitterBufferSetting.MaximumP25Milliseconds,
                settings.P25Adaptive),
            FneTrafficProtocol.Dmr => CreateConfiguration(
                DmrPacketDuration,
                settings.DmrMilliseconds,
                RxJitterBufferSetting.MaximumDmrMilliseconds,
                settings.DmrAdaptive),
            FneTrafficProtocol.Nxdn => CreateConfiguration(
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
