// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;
using DvmConsole.Media;
using System.Diagnostics;

namespace DvmConsole.Application;

internal sealed record ApplicationAudioConfiguration(
    AudioProcessingMode ProcessingMode,
    string InputDeviceId,
    string OutputDeviceId);

// Creates audio backends for the configured application route.
internal sealed class ApplicationAudioBackendProvider : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly Func<ApplicationAudioConfiguration, IAudioBackend> createNativeBackend;
    private readonly List<WeakReference<IImmediateAudioStop>> immediateStops = [];
    private ApplicationAudioConfiguration configuration;
    private bool immediateStopRequested;
    private bool disposed;

    public ApplicationAudioBackendProvider(
        ApplicationAudioConfiguration configuration,
        Func<ApplicationAudioConfiguration, IAudioBackend> createNativeBackend)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.createNativeBackend = createNativeBackend ?? throw new ArgumentNullException(nameof(createNativeBackend));
    }

    public IAudioBackend CreateBackend()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return new TrackedAudioBackend(
                createNativeBackend(configuration),
                TrackImmediateStop);
        }
    }

    public IReadOnlyList<Exception> StopImmediately()
    {
        IImmediateAudioStop[] endpoints;
        lock (sync)
        {
            immediateStopRequested = true;
            endpoints = CollectLiveEndpoints();
        }

        List<Exception>? failures = null;
        foreach (IImmediateAudioStop endpoint in endpoints)
        {
            try
            {
                endpoint.StopImmediately();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }
        return failures ?? [];
    }

    public async Task ReconfigureAsync(ApplicationAudioConfiguration next)
    {
        ArgumentNullException.ThrowIfNull(next);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (configuration == next)
                return;

            configuration = next;
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
        }
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    private bool TrackImmediateStop(object endpoint)
    {
        IImmediateAudioStop? immediateStop = endpoint as IImmediateAudioStop;
        bool stopNow;
        lock (sync)
        {
            if (immediateStop is not null)
            {
                bool alreadyTracked = false;
                for (int index = immediateStops.Count - 1; index >= 0; index--)
                {
                    if (!immediateStops[index].TryGetTarget(out IImmediateAudioStop? tracked))
                    {
                        immediateStops.RemoveAt(index);
                        continue;
                    }
                    alreadyTracked |= ReferenceEquals(tracked, immediateStop);
                }
                if (!alreadyTracked)
                    immediateStops.Add(new WeakReference<IImmediateAudioStop>(immediateStop));
            }
            stopNow = immediateStopRequested;
        }
        if (!stopNow)
            return false;

        immediateStop?.StopImmediately();
        return true;
    }

    private IImmediateAudioStop[] CollectLiveEndpoints()
    {
        var endpoints = new List<IImmediateAudioStop>(immediateStops.Count);
        for (int index = immediateStops.Count - 1; index >= 0; index--)
        {
            if (immediateStops[index].TryGetTarget(out IImmediateAudioStop? endpoint))
                endpoints.Add(endpoint);
            else
                immediateStops.RemoveAt(index);
        }
        return endpoints.ToArray();
    }

    private sealed class TrackedAudioBackend(
        IAudioBackend inner,
        Func<object, bool> trackEndpoint) :
        IAudioBackend,
        IDefaultAudioDeviceIdentityProvider
    {
        private int disposed;

        public string Name => inner.Name;

        public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
            => inner.EnumerateDevices(direction);

        public string? GetDefaultDeviceIdentity(AudioDirection direction)
            => inner is IDefaultAudioDeviceIdentityProvider identityProvider
                ? identityProvider.GetDefaultDeviceIdentity(direction)
                : null;

        public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            IAudioCapture capture = inner.OpenCapture(device, format);
            return TrackOrReject(capture);
        }

        public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            IAudioPlayback playback = inner.OpenPlayback(device, format);
            return TrackOrReject(playback);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                inner.Dispose();
        }

        private T TrackOrReject<T>(T endpoint)
            where T : IAsyncDisposable
        {
            try
            {
                if (!trackEndpoint(endpoint))
                    return endpoint;
            }
            catch
            {
                ObserveDisposal(endpoint);
                throw;
            }

            ObserveDisposal(endpoint);
            throw new ObjectDisposedException(
                nameof(ApplicationAudioBackendProvider),
                "The application audio safety fence has already closed new routes.");
        }

        private static void ObserveDisposal(IAsyncDisposable endpoint)
            => _ = ObserveDisposalAsync(endpoint);

        private static async Task ObserveDisposalAsync(IAsyncDisposable endpoint)
        {
            try
            {
                await endpoint.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Trace.TraceError(
                    "An audio endpoint rejected after the final safety fence failed to dispose: {0}",
                    exception);
            }
        }
    }
}
