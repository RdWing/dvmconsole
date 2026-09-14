// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Diagnostics;
using DvmConsole.Core.Runtime;
using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel : IReceiveIngressPresentation, IReceiveMediaPresentation
{
    void IReceiveIngressPresentation.RecordIngress(ReceiveIngressSystem source, IRadioMediaFrame frame)
    {
        SystemViewModel system = receiveSystems[source.Id].View;
        var traffic = (FneTrafficFrame)frame;
        system.RecordTraffic(traffic, publishDiagnostics: false);
    }
    void IReceiveIngressPresentation.Log(DateTimeOffset timestamp, string source, DebugLogSeverity severity, string message)
        => PostToUi(() => AddDebugLog(timestamp, source, severity, message));

    void IReceiveMediaPresentation.ReportDroppedFrame(ChannelId channel) { }
    void IReceiveMediaPresentation.PublishDiagnostics(ChannelId channel, uint streamId, DateTimeOffset now)
        => PublishReceiveDiagnostics(channel, streamId, now);
    void IReceiveMediaPresentation.PlaybackChanged(ChannelId channel) { }
    void IReceiveMediaPresentation.PublishFinalJitterSummary(ChannelId channel, uint streamId)
        => PublishFinalReceiveJitterSummary(channel, streamId);
    void IReceiveMediaPresentation.ReportCleanupFailure(ChannelId channel, uint streamId, Exception failure)
        => AddDebugLog(DateTimeOffset.UtcNow, "RX", DebugLogSeverity.Warning,
            $"RX audio cleanup failed for {ResolveChannel(channel).Name}, stream {streamId}: {failure.Message}");
}
