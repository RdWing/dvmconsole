// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Diagnostics;
using DvmConsole.Core.Runtime;
using DvmConsole.Media;
using DvmConsole.Operations;

namespace DvmConsole.Application;

public sealed partial class ConsoleReceiveSession : IReceiveOutputView, IReceiveOutputLifetimePort,
    IReceiveFrameObservationPort, IReceiveIngressPresentation, IReceiveMediaPresentation,
    IReceiveChannelTrafficPort, IReceiveCallHistoryPresentation, IReceiveEpisodeRetirementPort
{
    void IReceiveOutputView.ReceiveSelectionChanged(ChannelId channel, bool previous, bool enabled) => Changed(channel);
    Task IReceiveOutputView.RunAsync(Action action) { action(); return Task.CompletedTask; }
    void IReceiveOutputView.SetSelectionPreference(ChannelId channel, bool enabled) { }
    void IReceiveOutputView.StopRecording(ChannelId channel) => dependencies.Recordings?.StopChannel(DescribeRecording(channel));
    void IReceiveOutputView.NotifyMuteChanged() => Changed();
    void IReceiveOutputView.PublishStatus(string text) => SetStatus(text);
    DateTimeOffset IReceiveOutputLifetimePort.UtcNow => dependencies.Host.Clock.UtcNow;
    long IReceiveOutputLifetimePort.GetTimestamp() => time.GetTimestamp();
    TimeSpan IReceiveOutputLifetimePort.GetElapsedTime(long started) => time.GetElapsedTime(started);
    bool IReceiveOutputLifetimePort.IsDisposing => IsStopping;
    void IReceiveOutputLifetimePort.ObserveRecovery(TimeSpan elapsed, string result)
    {
        runtimeHealth.ObserveRouteRecovery(elapsed, result);
        SetStatus(result);
    }
    void IReceiveFrameObservationPort.PublishDiagnostics(ChannelId channel, uint streamId, DateTimeOffset now)
        => receiveDiagnostics.Inspect(channel, streamId, now);
    void IReceiveFrameObservationPort.ShowFault(ChannelId channel, Exception exception)
        => ReportMediaFailure(state.Channels[channel].Identity.Name, exception);
    void IReceiveIngressPresentation.RecordIngress(ReceiveIngressSystem system, IRadioMediaFrame frame) { }
    void IReceiveIngressPresentation.Log(DateTimeOffset timestamp, string source, DebugLogSeverity severity, string message) => Log(timestamp, source, severity, message);
    void IReceiveMediaPresentation.ReportDroppedFrame(ChannelId channel)
        => SetStatus($"Receive queue dropped a frame on {state.Channels[channel].Identity.Name}.");
    void IReceiveMediaPresentation.PublishDiagnostics(ChannelId channel, uint streamId, DateTimeOffset now)
        => receiveDiagnostics.Inspect(channel, streamId, now);
    void IReceiveMediaPresentation.PlaybackChanged(ChannelId channel) => Changed(channel);
    void IReceiveMediaPresentation.PublishFinalJitterSummary(ChannelId channel, uint streamId)
        => receiveDiagnostics.Complete(channel, streamId, dependencies.Host.Clock.UtcNow);
    void IReceiveMediaPresentation.ReportCleanupFailure(ChannelId channel, uint streamId, Exception failure)
        => ReportMediaFailure(state.Channels[channel].Identity.Name, failure);
    void IReceiveChannelTrafficPort.Projected(ChannelId channel, ChannelReceiveProjectionResult result) => Changed(channel);
    void IReceiveChannelTrafficPort.IgnoredLate(ChannelId channel, uint streamId, DateTimeOffset now) { }
    void IReceiveChannelTrafficPort.HistoryChanged() => Changed();
    void IReceiveChannelTrafficPort.NonCallTerminator(SystemId system) { }
    void IReceiveChannelTrafficPort.Log(DateTimeOffset timestamp, string source, DebugLogSeverity severity, string message) => Log(timestamp, source, severity, message);
    string IReceiveCallHistoryPresentation.DescribeSignalQuality(IRadioMediaFrame frame) => "";
    void IReceiveCallHistoryPresentation.Project(ConsoleCallHistoryRecord record) { }
    void IReceiveCallHistoryPresentation.Log(DateTimeOffset timestamp, string source, DebugLogSeverity severity, string message) => Log(timestamp, source, severity, message);
    void IReceiveEpisodeRetirementPort.ProjectCompletion(ConsoleCallCompletion completion) { }
    void IReceiveEpisodeRetirementPort.ReportCompleted(DateTimeOffset now, string systemName, string message) => Log(now, systemName, DebugLogSeverity.Info, message);
    void IReceiveEpisodeRetirementPort.ReportFailure(ReceiveCallEpisodeSnapshot episode, Exception exception) => SetStatus(exception.Message);
    private void OnRadioLog(object? sender, DebugLogEntry entry)
        => Log(entry.Timestamp, entry.Source, entry.Severity, entry.Message);

    private void ObservePatchDiagnostic(PatchForwardingDiagnostic diagnostic)
    {
        if (diagnostic.IsFailure && diagnostic.Exception is { } exception)
            runtimeHealth.ObserveTransmitError(exception);
        Log(diagnostic.ObservedAt, "PATCH",
            diagnostic.IsFailure ? DebugLogSeverity.Warning : DebugLogSeverity.Debug, diagnostic.Message);
    }

    private void ReportMediaFailure(string source, Exception exception)
    {
        if (source == "TX") runtimeHealth.ObserveTransmitError(exception);
        Log(dependencies.Host.Clock.UtcNow, source, DebugLogSeverity.Error, exception.Message);
        SetStatus(exception.Message);
    }

    private void Log(DateTimeOffset timestamp, string source, DebugLogSeverity severity, string message)
    {
        if (IsStopping) return;
        var entry = new ConsoleLogEvent(timestamp,
            severity is DebugLogSeverity.Error or DebugLogSeverity.Fatal ? ConsoleLogLevel.Error :
            severity == DebugLogSeverity.Warning ? ConsoleLogLevel.Warning :
            severity == DebugLogSeverity.Debug ? ConsoleLogLevel.Debug : ConsoleLogLevel.Information, source, message);
        foreach (EventHandler<ConsoleLogEvent> observer in LogPublished?.GetInvocationList() ?? [])
        {
            try { observer(this, entry); }
            catch { /* Diagnostics must not terminate receive work. */ }
        }
    }
}
