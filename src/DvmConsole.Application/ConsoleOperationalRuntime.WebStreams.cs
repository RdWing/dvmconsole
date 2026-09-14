// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;

namespace DvmConsole.Application;

internal sealed record ConsoleWebPlaybackDependencies(
    Func<IAudioBackend> CreateAudioBackend, Func<string?> OutputDevice,
    Func<WebStreamPlaybackDescriptor, CancellationToken, Task<Stream>>? OpenStream = null,
    Func<Stream, CancellationToken, Task<IAudioPcmStreamReader>>? CreateDecoder = null,
    Func<CancellationToken, ValueTask<IAudioPlayback>>? OpenSharedOutput = null);

internal sealed partial class ConsoleOperationalRuntime
{
    public WebStreamPlaybackCoordinator? WebPlayback { get; private set; }

    public WebStreamPlaybackCoordinator InitializeWebPlayback(ConsoleWebPlaybackDependencies dependencies,
        Func<WebStreamPlaybackState, ValueTask> observeState)
    {
        if (WebPlayback is not null) throw new InvalidOperationException("Web playback is already initialized.");
        return WebPlayback = new(dependencies.CreateAudioBackend, dependencies.OutputDevice,
            dependencies.OpenStream, dependencies.CreateDecoder, observeState, dependencies.OpenSharedOutput);
    }

    // Reserve this at the existing host registration point. Adapter intent and
    // subscriptions retire first; native/network playback retires even if they fail.
    public void RegisterWebPlaybackOwnership(string name, Func<ValueTask> retireAdapter)
        => Register(256, () => services.Audio.Register(name, async () =>
        {
            var cleanup = new AsyncCleanup();
            await cleanup.RunTaskAsync(() => retireAdapter().AsTask()).ConfigureAwait(false);
            if (WebPlayback is not null)
                await cleanup.RunTaskAsync(() => WebPlayback.DisposeAsync().AsTask()).ConfigureAwait(false);
            cleanup.ThrowIfFailed();
        }));
}
