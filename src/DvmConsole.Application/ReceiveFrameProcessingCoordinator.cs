// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;

namespace DvmConsole.Application;

internal interface IReceiveFrameAudioPort
{
    bool IsRecordingEnabled(ChannelId channel);
    bool IsActive(ChannelId channel);
    Task EnsureRecordingAudioAsync(ChannelId channel, CancellationToken cancellationToken);
    Task<ReceiveAudioProcessTiming> ProcessAsync(ChannelId channel, IRadioMediaFrame traffic,
        RadioFrameEncryption? encryption, CancellationToken cancellationToken);
    bool IsDeviceFailure(Exception exception);
    void RequestRecovery(ChannelId channel, Exception failure);
    Task StopAsync(ChannelId channel, CancellationToken cancellationToken);
    void EndStream(ChannelId channel, uint streamId);
}

internal interface IReceiveFrameObservationPort
{
    void PublishDiagnostics(ChannelId channel, uint streamId, DateTimeOffset now);
    void ShowFault(ChannelId channel, Exception exception);
}

internal interface IReceiveRecordingTrafficPort
{
    void ObserveRecordingTraffic(ChannelId channel, IRadioMediaFrame traffic);
}

/// <summary>Orders TAR decoder readiness, frame processing, recovery, and episode observation.</summary>
internal sealed class ReceiveFrameProcessingCoordinator(
    IReceiveFrameAudioPort audio, IReceiveFrameObservationPort observation, IClock clock,
    IReceiveRecordingTrafficPort recording)
{
    public async Task<ReceiveProcessingStageTiming> ProcessAsync(ChannelId channel, IRadioMediaFrame traffic,
        RadioFrameEncryption? encryption, CancellationToken cancellationToken)
    {
        ReceiveProcessingStageTiming processingStages = default;
        try
        {
            // TAR can be selected without live playback. Keep incoming frames in
            // their ordered worker until the recording-only decoder is ready.
            if (audio.IsRecordingEnabled(channel) && !audio.IsActive(channel))
                await audio.EnsureRecordingAudioAsync(channel, cancellationToken).ConfigureAwait(false);
            if (!audio.IsActive(channel)) return default;
            ReceiveAudioProcessTiming timing = await audio.ProcessAsync(channel, traffic, encryption, cancellationToken).ConfigureAwait(false);
            processingStages = new(timing.SessionGateDelay, timing.SessionProcessingDuration,
                timing.EncryptedSessionProcessing, timing.Measured);
            observation.PublishDiagnostics(channel, traffic.StreamId, clock.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            if (audio.IsDeviceFailure(exception))
            {
                audio.RequestRecovery(channel, exception);
                return default;
            }
            observation.ShowFault(channel, exception);
            await audio.StopAsync(channel, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (RadioReceiveTrafficClassifier.IsTerminator(traffic))
                audio.EndStream(channel, traffic.StreamId);
            else
                recording.ObserveRecordingTraffic(channel, traffic);
        }
        return processingStages;
    }
}
