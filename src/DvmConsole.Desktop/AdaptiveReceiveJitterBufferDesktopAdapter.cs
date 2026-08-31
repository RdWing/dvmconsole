using DvmConsole.Application;
using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

internal static class AdaptiveReceiveJitterBufferDesktopAdapter
{
    public static ReceiveJitterBufferProfile GetProfile(
        this AdaptiveReceiveJitterBufferController controller,
        string connectionName,
        FneTrafficProtocol protocol,
        ReceiveJitterBufferConfiguration configuration)
        => controller.GetProfile(
            connectionName,
            FneReceiveWorkQueueAdapter.ToRadioProtocol(protocol),
            configuration);

    public static void Observe(
        this AdaptiveReceiveJitterBufferController controller,
        string connectionName,
        FneTrafficFrame traffic,
        ReceiveJitterBufferConfiguration configuration)
        => controller.Observe(
            connectionName,
            traffic,
            traffic.TransportIngressTimestamp,
            configuration);
}
