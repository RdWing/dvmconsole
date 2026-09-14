// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;

namespace DvmConsole.Application;

internal sealed partial class ConsoleOperationalRuntime
{
    public ChannelAudioMeterRuntime Meters { get; private set; } = null!;
    public RecordingPlaybackCoordinator? RecordingPlayback { get; private set; }

    public void InitializeMeters(Action<ChannelAudioMeterUpdate> receive,
        Action<ChannelAudioMeterUpdate> transmit, TimeProvider? timeProvider = null)
    {
        if (Meters is not null) throw new InvalidOperationException("Meters are already initialized.");
        Meters = new(receive, transmit, timeProvider);
    }

    public void InitializeRecordingPlayback(IRecordingStore store,
        Func<IAudioBackend> createBackend, Func<string?> outputDevice,
        Action<Exception>? fault = null,
        Action<RecordingPlaybackStartupMetrics>? startup = null,
        Func<CancellationToken, ValueTask<IAudioPlayback>>? openSharedOutput = null)
    {
        if (RecordingPlayback is not null)
            throw new InvalidOperationException("Recording playback is already initialized.");
        RecordingPlayback = new(store, createBackend, outputDevice, fault, startup,
            openSharedOutput: openSharedOutput);
    }

    public void RegisterRecordingPlaybackOwnership(string name, Action? detach = null)
        => Register(64, () => services.Recording.Register(name, () =>
        {
            detach?.Invoke();
            return RecordingPlayback?.DisposeAsync() ?? ValueTask.CompletedTask;
        }));
}
