using DvmConsole.Audio;
using DvmConsole.Core.Diagnostics;
using System.Diagnostics;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel
{
    private async Task StartTransmitAsync(ChannelViewModel channel)
    {
        await StartTransmitAsync([channel]).ConfigureAwait(false);
    }

    private async Task StartTransmitAsync(IReadOnlyCollection<ChannelViewModel> channels)
    {
        if (channels.Count == 0 || transmitCoordinator.ActiveChannel is not null)
            return;

        TransmitTarget[] targets = channels
            .Select(channel => new TransmitTarget(
                channel,
                Systems.FirstOrDefault(candidate => candidate.Name.Equals(
                    channel.Definition.SystemName,
                    StringComparison.OrdinalIgnoreCase))!))
            .ToArray();
        ChannelViewModel? missingSystemChannel = targets
            .FirstOrDefault(target => target.System is null)?.Channel;
        if (missingSystemChannel is not null)
        {
            TransmitStatusText = $"PTT unavailable: system '{missingSystemChannel.Definition.SystemName}' was not found.";
            return;
        }

        bool suppressMicrophoneForPermitTone = TalkPermitTone;
        try
        {
            var startupTimer = Stopwatch.StartNew();
            // Keep the Apple duplex unit alive across PTT so its output mix
            // remains the AEC reference and macOS does not repeatedly remove
            // and recreate the system microphone-mode control.
            if (userSettings.MuteRxAudioWhileTransmitting)
                await MuteReceiveAudioAsync("RX audio muted while transmitting.");

            // Bring capture, processing, and every selected call fully online.
            // When a permit tone is enabled, discard microphone frames until
            // the first real callback proves the selected capture path is
            // ready and the local readiness indication has completed.
            transmitCoordinator.SetMicrophoneAudioSuppressed(suppressMicrophoneForPermitTone);
            await Task.Run(() => transmitCoordinator.StartAsync(targets)).ConfigureAwait(false);
            if (suppressMicrophoneForPermitTone)
                await transmitCoordinator.WaitForMicrophoneReadyAsync().ConfigureAwait(false);
            ChannelViewModel[] activeChannels = transmitCoordinator.ActiveChannels.ToArray();
            await RunOnUiThreadAsync(() =>
            {
                foreach (ChannelViewModel channel in activeChannels)
                    channel.SetTransmitEnabled(true, transmitCoordinator.GetActiveStreamId(channel));
                foreach (ChannelViewModel channel in activeChannels)
                {
                    TransmitTarget target = targets.First(candidate => ReferenceEquals(candidate.Channel, channel));
                    uint streamId = transmitCoordinator.GetActiveStreamId(channel);
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
                        $"TX call started on {channel.Name}: {ProtocolFor(channel).ToString().ToUpperInvariant()} " +
                        $"{target.System.SourceId ?? 0}→{channel.Definition.DestinationId}, stream {streamId}" +
                        (secure ? ", secure." : ", clear."));
                    AddDebugLog(
                        DateTimeOffset.Now,
                        target.System.Name,
                        DebugLogSeverity.Debug,
                        $"Vocoder TX initialized for {channel.Name}: mode {channel.Definition.Mode}, " +
                        $"stream {streamId}, audio processing {userSettings.AudioProcessingMode}, " +
                        $"warm microphone {(userSettings.KeepTransmitMicrophoneWarm ? "enabled" : "disabled")}, " +
                        $"all TX paths ready in {startupTimer.Elapsed.TotalMilliseconds:0} ms.");
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
                        encryptionKeyId: keyId);
                }
                NotifyCallHistoryChanged();
                TransmitStatusText = activeChannels.Length == 1
                    ? $"Transmitting on {activeChannels[0].Name}."
                    : $"Transmitting on {activeChannels.Length} selected channels.";
            }).ConfigureAwait(false);
            // A permit tone is an operational readiness indication. Play it
            // only after every selected call and the shared microphone path
            // have started successfully. In Apple processing mode this also
            // lets Voice Processing I/O claim and initialize the duplex route
            // before the local permit-tone playback path is opened.
            if (suppressMicrophoneForPermitTone)
            {
                await PlayTalkPermitToneAsync(
                    reportSuccess: false,
                    requiredForTransmit: true).ConfigureAwait(false);
                transmitCoordinator.SetMicrophoneAudioSuppressed(false);
            }
        }
        catch (Exception exception)
        {
            Exception startupFailure = exception;
            try
            {
                await Task.Run(() => transmitCoordinator.StopAsync()).ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                AddDebugLog(DateTimeOffset.Now, "TX", DebugLogSeverity.Warning,
                    $"Transmit startup cleanup also failed: {cleanupException.Message}");
            }
            finally
            {
                transmitCoordinator.SetMicrophoneAudioSuppressed(false);
            }

            try
            {
                await RestoreSuspendedAudioAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                AddDebugLog(DateTimeOffset.Now, "TX", DebugLogSeverity.Warning,
                    $"Receive-audio restoration after transmit startup failure also failed: {cleanupException.Message}");
            }

            await RunOnUiThreadAsync(() =>
            {
                foreach (ChannelViewModel channel in channels)
                {
                    channel.SetTransmitEnabled(false);
                    callRecordings.StopTransmit(channel);
                }
                AddDebugLog(DateTimeOffset.Now, "TX", DebugLogSeverity.Error,
                    $"Transmit startup failed: {startupFailure}");
                TransmitStatusText = $"PTT unavailable: {startupFailure.Message}";
            }).ConfigureAwait(false);
        }
    }

    private async Task StopTransmitAsync(ChannelViewModel channel)
        => await StopTransmitAsync([channel]).ConfigureAwait(false);

    private async Task StopTransmitAsync(IReadOnlyCollection<ChannelViewModel> channels)
    {
        (ChannelViewModel Channel, uint StreamId)[] activeStreams = channels
            .Select(channel => (channel, transmitCoordinator.GetActiveStreamId(channel)))
            .Where(entry => entry.Item2 != 0)
            .ToArray();
        Exception? stopFailure = null;
        try
        {
            await Task.Run(() => transmitCoordinator.StopAsync()).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // A failed audio-device stop or final FNE terminator must release
            // the UI call state without escaping through an async-void PTT
            // pointer/key callback and terminating the desktop process.
            stopFailure = exception;
        }
        finally
        {
            try
            {
                await RestoreSuspendedAudioAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                stopFailure ??= exception;
            }
            await RunOnUiThreadAsync(() =>
            {
                foreach (ChannelViewModel channel in channels)
                {
                    channel.SetTransmitEnabled(false);
                    callRecordings.StopTransmit(channel);
                }
                foreach ((ChannelViewModel channel, uint streamId) in activeStreams)
                {
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
                if (activeStreams.Length > 0)
                    NotifyCallHistoryChanged();
                TransmitStatusText = stopFailure is null
                    ? "PTT idle."
                    : $"Transmission stopped safely after an error: {stopFailure.Message}";
            }).ConfigureAwait(false);
        }
    }

    public async Task TestTalkPermitToneAsync()
        => await PlayTalkPermitToneAsync(reportSuccess: true).ConfigureAwait(false);

    private async Task PlayTalkPermitToneAsync(
        bool reportSuccess,
        bool requiredForTransmit = false)
    {
        try
        {
            LocalTonePlaybackResult result = await localTonePlayer.PlayAsync(LocalToneCues.TalkPermit).ConfigureAwait(false);
            string drainText = result.QueuedSamples is int queued &&
                               result.ConsumedSamples is int consumed
                ? $" queued {queued} / consumed {consumed} samples"
                : string.Empty;
            await RunOnUiThreadAsync(() => AddDebugLog(
                DateTimeOffset.Now,
                "TX",
                DebugLogSeverity.Debug,
                $"Talk permit tone completed on {result.Output.Name} ({result.Output.Id}) " +
                $"after {result.Attempts} playback attempt(s).{drainText}")).ConfigureAwait(false);
            if (reportSuccess)
            {
                await RunOnUiThreadAsync(() =>
                    AudioStatusText = $"Talk permit tone sent to {result.Output.Name}.{drainText}").ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            await RunOnUiThreadAsync(() =>
            {
                AddDebugLog(
                    DateTimeOffset.Now,
                    "TX",
                    DebugLogSeverity.Warning,
                    $"Talk permit tone unavailable: {exception}");
                AudioStatusText = $"Talk permit tone unavailable: {exception.Message}";
            }).ConfigureAwait(false);
            if (requiredForTransmit)
            {
                throw new InvalidOperationException(
                    "The talk-permit tone could not be completed, so microphone audio remained muted.",
                    exception);
            }
        }
    }

    private async Task RestoreSuspendedAudioAsync()
    {
        ChannelViewModel[] channels = suspendedAudioChannels;
        bool keptActive = suspendedAudioKeptActive;
        suspendedAudioChannels = [];
        suspendedAudioKeptActive = false;
        foreach (ChannelViewModel channel in channels)
        {
            if (!channel.IsAudioSuspended)
                continue;

            if (keptActive && audioCoordinator.IsActive(channel))
            {
                await audioCoordinator.SetGainAsync(channel, GetChannelVolume(channel)).ConfigureAwait(false);
                await RunOnUiThreadAsync(() => channel.SetAudioSuspended(false)).ConfigureAwait(false);
            }
            else
                await StartAudioAsync(channel).ConfigureAwait(false);
        }
    }

    private async Task MuteReceiveAudioAsync(string statusText)
    {
        ChannelViewModel[] receivingChannels = audioCoordinator.ActiveChannels.ToArray();
        if (receivingChannels.Length == 0)
            return;

        suspendedAudioChannels = receivingChannels;
        suspendedAudioKeptActive = false;
        await RunOnUiThreadAsync(() =>
        {
            foreach (ChannelViewModel receivingChannel in receivingChannels)
                receivingChannel.SetAudioSuspended(true);
        }).ConfigureAwait(false);

        await audioCoordinator.StopAsync().ConfigureAwait(false);

        await RunOnUiThreadAsync(() => AudioStatusText = statusText).ConfigureAwait(false);
    }

}
