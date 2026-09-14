// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

public sealed partial class ConsoleReceiveSession
{
    private ConsoleRecordingRuntime recordingRuntime => operationalRuntime.Recording;
    private ChannelRecordingCommands recordingControls => operationalRuntime.RecordingControls;
    private ReceiveRecordingTargetIndex recordingTargets => recordingRuntime.Targets;

    public IConsoleRecordingArchive CreateRecordingArchive(IConsoleRecordingArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        return new HistoryRecordingArchive(archive, state.History);
    }

    public ValueTask CheckpointAsync(CancellationToken cancellationToken = default)
        => RunCommandAsync(async token =>
        {
            // Joining the command gate also joins earlier preference writes.
            // The recording queue then flushes every earlier accepted sample.
            if (dependencies.Recordings is not { } capture) return;
            try { await capture.CheckpointAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                SetStatus($"Recording checkpoint failed: {exception.Message}");
                throw;
            }
        }, cancellationToken);

    private ConsoleLiveRecordingPorts PrepareRecording()
    {
        operationalRuntime.RegisterRecordingOwnership(services.Presentation);
        return new(state.ReceiveEpisodes, state.History, dependencies.Recordings, DescribeRecording,
            () => { lock (ingressSync) if (!IsStopping) Changed(); });
    }

    private ChannelRecordingDescriptor DescribeRecording(ChannelId id)
        => state.Channels[id].CaptureRecordingDescriptor(presentedReceive: true);

    public bool CanRecord(ChannelId id)
    {
        var channel = state.Channels[id];
        return !IsStopping && dependencies.Recordings is { } capture &&
            (channel.Operator.Snapshot.RecordingEnabled || (capture.CanWrite &&
                transmitChannels.CanListen(id)));
    }

    public ValueTask SetRecordingEnabledAsync(ChannelId id, bool enabled, CancellationToken cancellationToken = default)
        => RunCommandAsync(async token =>
        {
            if (dependencies.Recordings is null) throw new NotSupportedException("Recording is unavailable in this session.");
            if (!await recordingControls.SetEnabledAsync(id, enabled, token).ConfigureAwait(false))
                throw new InvalidOperationException("Recording is unavailable for this channel or storage location.");
        }, cancellationToken);

    private async Task ReconcileRecordingSelectionAsync(ChannelId id, bool enabled, CancellationToken token)
    {
        if (enabled) await receive.EnsureRecordingAudioAsync(id, token).ConfigureAwait(false);
        else
        {
            dependencies.Recordings!.StopChannel(DescribeRecording(id));
            await dependencies.Recordings.DrainAsync(token).ConfigureAwait(false);
            if (!state.Channels[id].Operator.Snapshot.AudioEnabled)
            {
                await receive.Work.StopAsync(id).WaitAsync(token).ConfigureAwait(false);
                await receive.Audio.StopAsync(id, token).ConfigureAwait(false);
            }
        }
    }
}
