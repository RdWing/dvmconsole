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

    private async Task StartTransmitAsync(IReadOnlyCollection<ChannelViewModel> channels)
    {
        // Reject a PTT edge while tones or another startup own admission; never
        // queue a stale press to transmit after the operator has released it.
        if (!await transmitAdmissionGate.WaitAsync(0).ConfigureAwait(false))
        {
            await RunOnUiThreadAsync(() => TransmitStatusText = "PTT unavailable while another transmit operation is in progress.")
                .ConfigureAwait(false);
            return;
        }
        try
        {
            await StartTransmitCoreAsync(channels).ConfigureAwait(false);
        }
        finally
        {
            transmitAdmissionGate.Release();
        }
    }

    private async Task StartTransmitCoreAsync(IReadOnlyCollection<ChannelViewModel> channels)
    {
        if (IsSessionInputSuppressed ||
            Volatile.Read(ref disposeStarted) != 0)
        {
            return;
        }

        if (networkDisabledDemo)
        {
            await RunOnUiThreadAsync(() =>
                TransmitStatusText = "Demo safety boundary: PTT input observed; network output remains disabled.")
                .ConfigureAwait(false);
            return;
        }

        if (channels.Count == 0 || transmitCoordinator.ActiveChannel is not null)
            return;

        ChannelViewModel? receivingChannel = channels.FirstOrDefault(
            channel => channel.IsReceivePresentationActive && !channel.HasCallPriority);
        if (receivingChannel is not null)
        {
            await RunOnUiThreadAsync(() =>
                TransmitStatusText = $"PTT unavailable: {receivingChannel.Name} is currently receiving.")
                .ConfigureAwait(false);
            return;
        }

        TransmitTarget[] targets = channels
            .Select(channel => new TransmitTarget(
                channel.ToTransmitDescriptor(),
                Systems.FirstOrDefault(candidate => candidate.Name.Equals(
                    channel.Definition.SystemName,
                    StringComparison.OrdinalIgnoreCase))!))
            .ToArray();
        TransmitChannelDescriptor? missingSystemChannel = targets
            .FirstOrDefault(target => target.System is null)?.Channel;
        if (missingSystemChannel is not null)
        {
            TransmitStatusText = $"PTT unavailable: system '{missingSystemChannel.Definition.SystemName}' was not found.";
            return;
        }

        await RunOnUiThreadAsync(() =>
        {
            foreach (ChannelViewModel target in channels)
                target.SetTransmitStarting(true);
            TransmitStatusText = channels.Count == 1
                ? $"Starting PTT on {channels.First().Name}…"
                : $"Starting PTT on {channels.Count} selected channels…";
        }).ConfigureAwait(false);

        await transmitLifecycle.StartAsync(new TransmitStartRequest(targets, TalkPermitTone))
            .ConfigureAwait(false);
    }

    async Task ITransmitLifecyclePresentation.StartedAsync(
        IReadOnlyList<TransmitTarget> targets,
        IReadOnlyList<ChannelId> activeIds,
        TransmitStartupDiagnostics diagnostics,
        Stopwatch startupTimer)
    {
        ChannelViewModel[] activeChannels = ResolveChannels(activeIds);
        await RunOnUiThreadAsync(() =>
        {
            foreach (ChannelViewModel channel in activeChannels)
                channel.SetTransmitEnabled(
                    true,
                    transmitCoordinator.GetActiveStreamId(new ChannelId(channel.SessionId)));
            foreach (ChannelViewModel channel in activeChannels)
            {
                TransmitTarget target = targets.First(candidate =>
                    candidate.Channel.Id == new ChannelId(channel.SessionId));
                uint streamId = transmitCoordinator.GetActiveStreamId(new ChannelId(channel.SessionId));
                bool secure = channel.Definition.IsEncrypted && channel.IsTransmitEncrypted;
                byte? algorithmId = null;
                ushort? keyId = null;
                if (secure && EncryptionPresentation.TryParseConfiguredAlgorithm(
                        channel.Definition,
                        out byte parsedAlgorithmId,
                        out ushort parsedKeyId))
                {
                    algorithmId = parsedAlgorithmId;
                    keyId = parsedKeyId;
                }

                AddDebugLog(
                    DateTimeOffset.Now,
                    target.System.Name,
                    DebugLogSeverity.Info,
                    $"TX call started on {channel.Name}: " +
                    $"{ProtocolFor(channel).ToString().ToUpperInvariant()} " +
                    $"{target.System.SourceId ?? 0}→{channel.Definition.DestinationId}, " +
                    $"stream {streamId}" +
                    (secure ? ", secure." : ", clear."));
                AddDebugLog(
                    DateTimeOffset.Now,
                    target.System.Name,
                    DebugLogSeverity.Debug,
                    FormatTransmitStartupDiagnostics(
                        channel,
                        streamId,
                        diagnostics,
                        startupTimer.Elapsed));
                callHistory.AddConsoleTransmission(
                    DateTimeOffset.Now,
                    target.System.Name,
                    channel.Name,
                    target.System.SourceId ?? 0,
                    channel.Definition.DestinationId,
                    ProtocolFor(channel),
                    streamId,
                    callerText: "Console",
                    encrypted: secure,
                    encryptionAlgorithmId: algorithmId,
                    encryptionKeyId: keyId,
                    channelId: new ChannelId(channel.SessionId));
            }

            NotifyCallHistoryChanged();
            TransmitStatusText = activeChannels.Length == 1
                ? $"Transmitting on {activeChannels[0].Name} · PTT: {PttInputSourceText}."
                : $"Transmitting on {activeChannels.Length} selected channels · PTT: {PttInputSourceText}.";
        }).ConfigureAwait(false);
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

    private async Task StopTransmitAsync(
        IReadOnlyCollection<ChannelViewModel> channels,
        string? stoppedStatusText = null,
        bool propagateUnconfirmedStop = false)
    {
        await transmitAdmissionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopTransmitCoreAsync(channels, stoppedStatusText, propagateUnconfirmedStop).ConfigureAwait(false);
        }
        finally
        {
            transmitAdmissionGate.Release();
        }
    }

    private Task StopTransmitCoreAsync(
        IReadOnlyCollection<ChannelViewModel> channels,
        string? stoppedStatusText = null,
        bool propagateUnconfirmedStop = false)
        => transmitLifecycle.StopAsync(
            channels.Select(channel => new ChannelId(channel.SessionId)).ToArray(),
            stoppedStatusText, propagateUnconfirmedStop);

    async Task ITransmitLifecyclePresentation.StoppingAsync(IReadOnlyList<TransmitStream> streams)
    {
        var activeStreams = streams.Select(stream =>
            (Channel: ResolveChannel(stream.ChannelId), stream.StreamId)).ToArray();
        await RunOnUiThreadAsync(() =>
        {
            foreach (var entry in activeStreams)
                entry.Channel.SetTransmitStopping(true);
            TransmitStatusText = activeStreams.Length == 1
                ? $"Releasing PTT on {activeStreams[0].Channel.Name}…"
                : $"Releasing PTT on {activeStreams.Length} channels…";
        }).ConfigureAwait(false);
    }

    async Task ITransmitLifecyclePresentation.StoppedAsync(
        IReadOnlyList<ChannelId> channelIds,
        IReadOnlyList<TransmitStream> streams,
        IReadOnlySet<ChannelId> unresolved,
        TimeSpan elapsed,
        Exception? stopFailure,
        string? stoppedStatusText)
    {
        ChannelViewModel[] channels = ResolveChannels(channelIds);
        var activeStreams = streams.Select(stream =>
            (Channel: ResolveChannel(stream.ChannelId), stream.StreamId)).ToArray();
        await RunOnUiThreadAsync(() =>
        {
            foreach (ChannelViewModel channel in channels)
            {
                if (unresolved.Contains(new ChannelId(channel.SessionId)))
                {
                    channel.SetTransmitStopping(false);
                    continue;
                }
                channel.SetTransmitEnabled(false);
                callRecordings.StopTransmit(channel);
            }
            foreach ((ChannelViewModel channel, uint streamId) in activeStreams)
            {
                if (unresolved.Contains(new ChannelId(channel.SessionId)))
                    continue;
                SystemViewModel? system = Systems.FirstOrDefault(candidate => candidate.Channels.Contains(channel));
                if (system is not null)
                {
                    AddDebugLog(
                        DateTimeOffset.Now,
                        system.Name,
                        DebugLogSeverity.Info,
                        $"TX call ended on {channel.Name}: {ProtocolFor(channel).ToString().ToUpperInvariant()} " +
                        $"stream {streamId}.");
                    callHistory.CompleteConsoleTransmission(
                        system.Name,
                        ProtocolFor(channel),
                        streamId,
                        DateTimeOffset.Now,
                        channel.Name,
                        channel.Definition.DestinationId);
                }
            }
            if (activeStreams.Any(entry =>
                !unresolved.Contains(new ChannelId(entry.Channel.SessionId))))
            {
                NotifyCallHistoryChanged();
            }
            AddDebugLog(
                DateTimeOffset.Now,
                "TX",
                unresolved.Count == 0 ? DebugLogSeverity.Debug : DebugLogSeverity.Warning,
                $"PTT release completed in {elapsed.TotalMilliseconds:0} ms; " +
                $"{activeStreams.Count(entry => !unresolved.Contains(
                    new ChannelId(entry.Channel.SessionId)))} confirmed, " +
                $"{activeStreams.Count(entry => unresolved.Contains(
                    new ChannelId(entry.Channel.SessionId)))} unconfirmed.");
            TransmitStatusText = unresolved.Count > 0
                ? $"PTT release failed; transmit state remains active for retry or disconnect: " +
                  $"{stopFailure?.Message ?? "stop was not confirmed"}"
                : stopFailure is null
                ? stoppedStatusText ?? "PTT idle."
                : $"Transmission stopped safely after an error: {stopFailure.Message}";
        }).ConfigureAwait(false);
    }

    bool ITransmitLifecyclePresentation.MuteReceiveWhileTransmitting => userSettings.MuteRxAudioWhileTransmitting;
    bool? ITransmitLifecyclePresentation.SelectedMicrophoneIsBluetooth => SelectedAudioInputDevice?.IsBluetooth;
    void ITransmitLifecyclePresentation.ClearActivation() => pttActivationArbiter.Clear();
    void ITransmitLifecyclePresentation.Log(DateTimeOffset timestamp, string source, DebugLogSeverity severity, string message)
        => AddDebugLog(timestamp, source, severity, message);
    Task ITransmitLifecyclePresentation.StartFailedAsync(IReadOnlyList<ChannelId> channels, Exception failure)
        => RunOnUiThreadAsync(() =>
        {
            foreach (ChannelViewModel channel in ResolveChannels(channels))
            {
                channel.SetTransmitEnabled(false);
                callRecordings.StopTransmit(channel);
            }
            AddDebugLog(DateTimeOffset.Now, "TX", DebugLogSeverity.Error,
                $"Transmit startup failed: {failure}");
            TransmitStatusText = $"PTT unavailable: {failure.Message}";
        });

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

    private async Task MuteReceiveAudioAsync(string statusText)
        => await transmitAudioTransition.MuteReceiveAudioAsync(statusText).ConfigureAwait(false);

}
