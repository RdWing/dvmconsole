using DvmConsole.Application;
using DvmConsole.Core.Settings;
using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

internal static class ReceiveJitterBufferPolicy
{
    public static ReceiveJitterBufferConfiguration GetConfiguration(
        FneTrafficProtocol protocol,
        RxJitterBufferSetting settings)
        => ReceiveJitterBufferConfigurationPolicy.GetConfiguration(
            FneReceiveWorkQueueAdapter.ToRadioProtocol(protocol),
            settings);
}
