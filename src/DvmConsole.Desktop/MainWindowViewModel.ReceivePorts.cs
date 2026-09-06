// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Diagnostics;
using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel : IReceiveTrafficPresentation, IReceiveMediaPresentation
{
    bool IReceiveTrafficPresentation.InputsSuppressed => IsSessionInputSuppressed;
    bool IReceiveTrafficPresentation.IsTrackingStream(ChannelId channel, uint streamId)
        => ResolveChannel(channel).IsTrackingReceiveStream(streamId);
    void IReceiveTrafficPresentation.RecordIngress(SystemViewModel system, FneTrafficFrame traffic)
    {
        system.RecordTraffic(traffic, publishDiagnostics: false);
        ObserveAdaptiveReceiveJitter(system, traffic);
    }
    void IReceiveTrafficPresentation.Present(SystemViewModel system, SystemTrafficWorkItem workItem)
        => receivePresentation.Present(system, workItem);
    void IReceiveTrafficPresentation.ProjectLifecycle(ChannelId channel, ReceiveRouteProjectionDecision decision, DateTimeOffset now)
        => ProjectReceiveLifecycleDecision(ResolveChannel(channel), decision, now);
    void IReceiveTrafficPresentation.Log(DateTimeOffset timestamp, string source, DebugLogSeverity severity, string message)
        => AddDebugLog(timestamp, source, severity, message);

    void IReceiveMediaPresentation.RecordDroppedFrame(ChannelId channel) => ResolveChannel(channel).RecordDroppedReceiveFrame();
    void IReceiveMediaPresentation.PublishDiagnostics(ChannelId channel, uint streamId, DateTimeOffset now)
        => PublishReceiveDiagnostics(ResolveChannel(channel), streamId, now);
    void IReceiveMediaPresentation.MarkAudioMeter(ChannelId channel, uint streamId, bool ended)
    {
        if (ended)
            ResolveChannel(channel).MarkReceiveAudioMeterEnded(streamId);
        else
            ResolveChannel(channel).MarkReceiveAudioMeterActive(streamId);
    }
    void IReceiveMediaPresentation.MarkPlaybackActive(ChannelId channel, uint sourceId, uint streamId)
        => ResolveChannel(channel).MarkReceivePlaybackActive(sourceId, streamId);
    void IReceiveMediaPresentation.PublishFinalJitterSummary(ChannelId channel, uint streamId)
        => PublishFinalReceiveJitterSummary(ResolveChannel(channel), streamId);
    void IReceiveMediaPresentation.ReportCleanupFailure(ChannelId channel, uint streamId, Exception failure)
        => AddDebugLog(DateTimeOffset.UtcNow, "RX", DebugLogSeverity.Warning,
            $"RX audio cleanup failed for {ResolveChannel(channel).Name}, stream {streamId}: {failure.Message}");
}
