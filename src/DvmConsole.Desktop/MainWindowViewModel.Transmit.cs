// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;
using DvmConsole.Application;
using DvmConsole.Core.Diagnostics;
using System.Diagnostics;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel : ITransmitLifecyclePresentation
{
    private async Task StartTransmitAsync(ChannelViewModel channel)
    {
        await StartTransmitAsync([channel]).ConfigureAwait(false);
    }

    private Task StartTransmitAsync(IReadOnlyCollection<ChannelViewModel> channels)
        => transmitRuntime.Manual.StartAsync(channels.Select(channel => new ChannelId(channel.SessionId)).ToArray());

    private void PresentManualTransmitStarting(IReadOnlyList<ChannelId> ids)
        => SessionStatus.SetTransmit(ids.Count == 1
            ? $"Starting PTT on {ResolveChannel(ids[0]).Name}…"
            : $"Starting PTT on {ids.Count} selected channels…");

    Task ITransmitLifecyclePresentation.StartedAsync(
        IReadOnlyList<TransmitTarget> targets,
        IReadOnlyList<ChannelId> activeIds,
        TransmitStartupDiagnostics diagnostics,
        Func<TimeSpan> getStartupElapsed)
    {
        PostToUi(NotifyCallHistoryChanged);
        SessionStatus.SetTransmit(activeIds.Count == 1
            ? $"Transmitting on {ResolveChannel(activeIds[0]).Name} · PTT: {PttInputSourceText}."
            : $"Transmitting on {activeIds.Count} selected channels · PTT: {PttInputSourceText}.");
        return Task.CompletedTask;
    }

    internal ManualTransmitSession ManualTransmitSession => transmitRuntime.Session;

    private void PresentManualTransmitStarted(TransmitTarget target, uint streamId,
        ConsoleCallHistoryRecord record, TransmitStartupDiagnostics diagnostics, Func<TimeSpan> getStartupElapsed)
    {
        ChannelViewModel channel = ResolveChannel(target.Channel.Id);
        bool secure = target.Channel.Definition.IsEncrypted && target.Channel.TransmitEncrypted;
        PostToUi(() =>
        {
            AddDebugLog(DateTimeOffset.Now, target.System.Name, DebugLogSeverity.Info,
                $"TX call started on {channel.Name}: " +
                $"{ProtocolFor(channel).ToString().ToUpperInvariant()} " +
                $"{target.System.SourceId ?? 0}→{channel.Definition.DestinationId}, " +
                $"stream {streamId}" + (secure ? ", secure." : ", clear."));
            AddDebugLog(DateTimeOffset.Now, target.System.Name, DebugLogSeverity.Debug,
                FormatTransmitStartupDiagnostics(channel, streamId, diagnostics, getStartupElapsed()));
            // Read back the latest record if TX ended before the UI caught up.
            if (callHistory.Runtime.Find(record.Id) is { } current)
                callHistory.ProjectRuntimeRecord(current);
        });
    }

    private string FormatTransmitStartupDiagnostics(
        ChannelViewModel channel,
        uint streamId,
        TransmitStartupDiagnostics diagnostics,
        TimeSpan channelPresentationAt)
    {
        string captureTiming = diagnostics.MicrophoneReadiness is null
            ? "microphone readiness not gated"
            : $"capture start returned " +
              $"{diagnostics.MicrophoneReadiness.CaptureStartReturned.TotalMilliseconds:0} ms, " +
              $"first samples " +
              $"{diagnostics.MicrophoneReadiness.FirstSamplesReceived.TotalMilliseconds:0} ms";
        return $"Vocoder TX initialized for {channel.Name}: mode {channel.Definition.Mode}, " +
               $"stream {streamId}, audio processing {userSettings.AudioProcessingMode}, " +
               $"warm microphone {(userSettings.KeepTransmitMicrophoneWarm ? "enabled" : "disabled")}, " +
               $"TX sessions {diagnostics.TransmitSessionsReadyAt.TotalMilliseconds:0} ms, " +
               $"cue barrier {diagnostics.CueBarrierReleasedAt.TotalMilliseconds:0} ms, " +
               $"selected microphone {diagnostics.MicrophoneReadyAt.TotalMilliseconds:0} ms " +
               $"({(diagnostics.MicrophoneStartedCold ? "cold" : "warm")}, " +
               $"Bluetooth {DescribeBluetoothState(diagnostics.MicrophoneIsBluetooth)}), " +
               $"{captureTiming}, " +
               $"channel presentation {channelPresentationAt.TotalMilliseconds:0} ms.";
    }

    private async Task StopTransmitAsync(ChannelViewModel channel)
        => await StopTransmitAsync([channel]).ConfigureAwait(false);

    private Task StopTransmitAsync(
        IReadOnlyCollection<ChannelViewModel> channels,
        string? stoppedStatusText = null,
        bool propagateUnconfirmedStop = false)
        => transmitRuntime.Manual.StopAsync(
            channels.Select(channel => new ChannelId(channel.SessionId)).ToArray(),
            stoppedStatusText, propagateUnconfirmedStop);

    Task ITransmitLifecyclePresentation.StoppingAsync(IReadOnlyList<TransmitStream> streams)
    {
        SessionStatus.SetTransmit(streams.Count == 1
            ? $"Releasing PTT on {ResolveChannel(streams[0].ChannelId).Name}…"
            : $"Releasing PTT on {streams.Count} channels…");
        return Task.CompletedTask;
    }

    Task ITransmitLifecyclePresentation.StoppedAsync(
        IReadOnlyList<ChannelId> channelIds,
        IReadOnlyList<TransmitStream> streams,
        IReadOnlySet<ChannelId> unresolved,
        TimeSpan elapsed,
        Exception? stopFailure,
        string? stoppedStatusText)
    {
        int completed = streams.Count(stream => !unresolved.Contains(stream.ChannelId));
        if (completed > 0) PostToUi(NotifyCallHistoryChanged);
        int unconfirmed = streams.Count - completed;
        DebugLogSeverity releaseSeverity = unresolved.Count == 0 ? DebugLogSeverity.Debug : DebugLogSeverity.Warning;
        PostToUi(() => AddDebugLog(DateTimeOffset.Now, "TX",
            releaseSeverity,
            $"PTT release completed in {elapsed.TotalMilliseconds:0} ms; " +
            $"{completed} confirmed, {unconfirmed} unconfirmed."));
        SessionStatus.SetTransmit(unresolved.Count > 0
            ? $"PTT release failed; transmit state remains active for retry or disconnect: " +
              $"{stopFailure?.Message ?? "stop was not confirmed"}"
            : stopFailure is null
            ? stoppedStatusText ?? "PTT idle."
            : $"Transmission stopped safely after an error: {stopFailure.Message}");
        return Task.CompletedTask;
    }

    private void PresentManualTransmitCompleted(TransmitStream stream, CallId? call)
    {
        var definition = channelMedia.State(stream.ChannelId).Runtime.Definition;
        string system = definition.SystemName;
        PostToUi(() =>
        {
            AddDebugLog(DateTimeOffset.Now, system, DebugLogSeverity.Info,
                $"TX call ended on {definition.Name}: {ChannelProtocolMediaMapper.ToTrafficProtocol(definition.Protocol).ToString().ToUpperInvariant()} " +
                $"stream {stream.StreamId}.");
            if (call is { } id && callHistory.Runtime.Find(id) is { } record)
                callHistory.ProjectRuntimeRecord(record);
        });
    }

    DateTimeOffset ITransmitLifecyclePresentation.Now => DateTimeOffset.Now;
    long ITransmitLifecyclePresentation.GetTimestamp() => Stopwatch.GetTimestamp();
    TimeSpan ITransmitLifecyclePresentation.GetElapsedTime(long started) => Stopwatch.GetElapsedTime(started);
    bool ITransmitLifecyclePresentation.MuteReceiveWhileTransmitting => userSettings.MuteRxAudioWhileTransmitting;
    bool? ITransmitLifecyclePresentation.SelectedMicrophoneIsBluetooth => SelectedAudioInputDevice?.IsBluetooth;
    void ITransmitLifecyclePresentation.ClearActivation() => pttActivationArbiter.Clear();
    void ITransmitLifecyclePresentation.Log(DateTimeOffset timestamp, string source, DebugLogSeverity severity, string message)
        => AddDebugLog(timestamp, source, severity, message);
    Task ITransmitLifecyclePresentation.StartFailedAsync(IReadOnlyList<ChannelId> channels, Exception failure)
    {
        PostToUi(() => AddDebugLog(DateTimeOffset.Now, "TX", DebugLogSeverity.Error,
            $"Transmit startup failed: {failure}"));
        SessionStatus.SetTransmit($"PTT unavailable: {failure.Message}");
        return Task.CompletedTask;
    }

    public async Task TestTalkPermitToneAsync()
        => await transmitAudioTransition.PlayTalkPermitToneAsync(reportSuccess: true).ConfigureAwait(false);

    private static string DescribeBluetoothState(bool? isBluetooth)
        => isBluetooth switch
        {
            true => "yes",
            false => "no",
            null => "unknown"
        };

    private async Task RestoreSuspendedAudioAsync()
        => await transmitAudioTransition.RestoreSuspendedAudioAsync().ConfigureAwait(false);

}
