using DvmConsole.Core.Settings;
using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

internal readonly record struct ReceiveJitterBufferProfile(
    TimeSpan PacketDuration,
    TimeSpan TargetDelay)
{
    public bool IsEnabled => TargetDelay > TimeSpan.Zero;
}

internal static class ReceiveJitterBufferPolicy
{
    private static readonly TimeSpan P25PacketDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan DmrPacketDuration = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan NxdnPacketDuration = TimeSpan.FromMilliseconds(80);

    public static ReceiveJitterBufferProfile GetProfile(
        FneTrafficProtocol protocol,
        RxJitterBufferSetting settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return protocol switch
        {
            FneTrafficProtocol.P25 => new(
                P25PacketDuration,
                TimeSpan.FromMilliseconds(settings.P25Milliseconds)),
            FneTrafficProtocol.Dmr => new(
                DmrPacketDuration,
                TimeSpan.FromMilliseconds(settings.DmrMilliseconds)),
            FneTrafficProtocol.Nxdn => new(
                NxdnPacketDuration,
                TimeSpan.FromMilliseconds(settings.NxdnMilliseconds)),
            _ => default
        };
    }
}
