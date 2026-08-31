using DvmConsole.Application;
using DvmConsole.Core.Runtime;
using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

internal static class FneReceiveWorkQueueAdapter
{
    public static bool Enqueue(
        this ChannelReceiveWorkQueue queue,
        ChannelId channelId,
        FneTrafficFrame traffic)
        => queue.Enqueue(channelId, CreateIngress(traffic, traffic.FneBoundaryTimestamp));

    public static bool Enqueue(
        this ChannelReceiveWorkQueue queue,
        ChannelId channelId,
        FneTrafficFrame traffic,
        out bool droppedFrame)
        => queue.Enqueue(
            channelId,
            CreateIngress(traffic, traffic.FneBoundaryTimestamp),
            out droppedFrame);

    public static bool Enqueue(
        this ChannelReceiveWorkQueue queue,
        ChannelId channelId,
        FneTrafficFrame traffic,
        long applicationBoundaryTimestamp,
        out bool droppedFrame)
        => queue.Enqueue(
            channelId,
            CreateIngress(traffic, applicationBoundaryTimestamp),
            out droppedFrame);

    public static FneTrafficProtocol ToFneProtocol(RadioMediaProtocol protocol)
        => protocol switch
        {
            RadioMediaProtocol.Dmr => FneTrafficProtocol.Dmr,
            RadioMediaProtocol.P25 => FneTrafficProtocol.P25,
            RadioMediaProtocol.Nxdn => FneTrafficProtocol.Nxdn,
            RadioMediaProtocol.Analog => FneTrafficProtocol.Analog,
            _ => throw new ArgumentOutOfRangeException(nameof(protocol))
        };

    public static RadioMediaProtocol ToRadioProtocol(FneTrafficProtocol protocol)
        => protocol switch
        {
            FneTrafficProtocol.Dmr => RadioMediaProtocol.Dmr,
            FneTrafficProtocol.P25 => RadioMediaProtocol.P25,
            FneTrafficProtocol.Nxdn => RadioMediaProtocol.Nxdn,
            FneTrafficProtocol.Analog => RadioMediaProtocol.Analog,
            _ => throw new ArgumentOutOfRangeException(nameof(protocol))
        };

    private static RadioMediaIngressFrame CreateIngress(
        FneTrafficFrame traffic,
        long applicationBoundaryTimestamp)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        return new RadioMediaIngressFrame(
            traffic,
            applicationBoundaryTimestamp,
            traffic.TransportIngressTimestamp);
    }
}
