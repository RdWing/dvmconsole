// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Operations;

namespace DvmConsole.Application;

public sealed partial class ConsoleReceiveSession : IConsoleEngineeringHealth
{
    private readonly RuntimeHealthController runtimeHealth;
    private static readonly MicrophoneHealth StoppedMicrophone = new(
        MicrophoneHealthState.Stopped, 0, null, null, null);

    public async Task<RuntimeHealthSnapshot> CaptureHealthAsync(CancellationToken cancellationToken = default)
    {
        RuntimeHealthSnapshot? snapshot = null;
        await RunCommandAsync(async token =>
        {
            // Spool health can inspect storage. Keep that work off the UI thread
            // and inside the session lifetime; the controller throttles refreshes.
            snapshot = await Task.Run(() =>
            {
                if (dependencies.Recordings is IRecordingCatalogHealthSource { CatalogHealth: { } catalog })
                    runtimeHealth.ObserveRecordingCatalog(catalog);
                return runtimeHealth.Capture(new(
                    dependencies.Host.Clock.UtcNow,
                    receive.Work.CaptureHealth(), patches.Work.CaptureHealth(),
                    transmit?.Microphone.MicrophoneHealth ?? StoppedMicrophone,
                    transmit?.Microphone.QueueHealth ?? default,
                    patches.Forwarding.CaptureQueueHealth(),
                    transmit?.Microphone.IsMicrophoneAudioSuppressed ?? false));
            }, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return snapshot!;
    }
}
