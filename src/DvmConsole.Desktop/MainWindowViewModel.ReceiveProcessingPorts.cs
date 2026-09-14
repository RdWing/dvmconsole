// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Runtime;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel : IReceiveFrameObservationPort
{
    void IReceiveFrameObservationPort.PublishDiagnostics(ChannelId channel, uint streamId, DateTimeOffset now)
        => PublishReceiveDiagnostics(channel, streamId, now);
    void IReceiveFrameObservationPort.ShowFault(ChannelId channelId, Exception exception)
        => PostToUi(() =>
        {
            AddDebugLog(DateTimeOffset.Now, "RX", DvmConsole.Core.Diagnostics.DebugLogSeverity.Error,
                $"Receive processing failed on {ResolveChannel(channelId).Name}; RX and TAR selections retained. {exception}");
            AudioStatusText = $"RX interrupted on {ResolveChannel(channelId).Name}; selection retained, retrying: {exception.Message}";
        });
}
