// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;
using DvmConsole.Media;

namespace DvmConsole.Application;

/// <summary>
/// Shares one physical playback endpoint between web streams that target the
/// same device and format. Each caller receives an independently controlled
/// mixer lane and releases the physical endpoint with its last lease.
/// </summary>
internal sealed class WebStreamOutputRoutePool : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<RouteKey, Route> routes = [];
    private readonly AsyncDisposal disposal = new();
    private bool disposed;

    public async Task<IAudioPlayback> AcquireAsync(
        IAudioBackend backend,
        AudioDeviceInfo device,
        PcmAudioFormat format,
        string diagnosticLabel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(format);

        var key = new RouteKey(
            device.Id,
            format.SampleRate,
            format.Channels,
            format.BitsPerSample);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!routes.TryGetValue(key, out Route? route))
            {
                // Some platform backends synchronously initialize a native
                // audio graph here. Keep that work off the UI thread, while
                // serializing route creation so concurrent starts cannot open
                // duplicate physical endpoints.
                IAudioPlayback physical = await Task.Run(
                        () => backend.OpenPlayback(device, format),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    route = new Route(new AudioMixer(physical));
                    physical = null!;
                    routes.Add(key, route);
                }
                finally
                {
                    if (physical is not null)
                        await physical.DisposeAsync().ConfigureAwait(false);
                }
            }

            return route.Acquire(
                this,
                key,
                diagnosticLabel);
        }
        finally
        {
            gate.Release();
        }
    }

    public ValueTask DisposeAsync()
        => disposal.RunAsync(DisposeCoreAsync);

    private async Task ReleaseAsync(RouteKey key, Route route)
    {
        bool disposeRoute = false;
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (route.Release() == 0)
            {
                if (routes.TryGetValue(key, out Route? current) &&
                    ReferenceEquals(current, route))
                {
                    routes.Remove(key);
                }
                disposeRoute = true;
            }
        }
        finally
        {
            gate.Release();
        }

        if (disposeRoute)
            await route.DisposeAsync().ConfigureAwait(false);
    }

    private async Task DisposeCoreAsync()
    {
        Route[] oldRoutes;
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (disposed)
                return;
            disposed = true;
            oldRoutes = routes.Values.ToArray();
            routes.Clear();
        }
        finally
        {
            gate.Release();
        }

        Exception? failure = null;
        foreach (Route route in oldRoutes)
        {
            try
            {
                await route.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        gate.Dispose();
        if (failure is not null)
            throw failure;
    }

    private readonly record struct RouteKey(
        string DeviceId,
        int SampleRate,
        int Channels,
        int BitsPerSample)
    {
        public bool Equals(RouteKey other)
            => DeviceId.Equals(other.DeviceId, StringComparison.OrdinalIgnoreCase) &&
               SampleRate == other.SampleRate &&
               Channels == other.Channels &&
               BitsPerSample == other.BitsPerSample;

        public override int GetHashCode()
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(DeviceId),
                SampleRate,
                Channels,
                BitsPerSample);
    }

    private sealed class Route(AudioMixer mixer) : IAsyncDisposable
    {
        private readonly AsyncDisposal disposal = new();
        private int leaseCount;

        public IAudioPlayback Acquire(
            WebStreamOutputRoutePool owner,
            RouteKey key,
            string diagnosticLabel)
        {
            IAudioPlayback lane = mixer.OpenChannel(diagnosticLabel);
            checked
            {
                leaseCount++;
            }
            return new Lease(owner, key, this, lane);
        }

        public int Release()
        {
            if (leaseCount <= 0)
                return 0;
            return --leaseCount;
        }

        public ValueTask DisposeAsync()
            => disposal.RunAsync(async () =>
                await mixer.DisposeAsync().ConfigureAwait(false));
    }

    private sealed class Lease(
        WebStreamOutputRoutePool owner,
        RouteKey key,
        Route route,
        IAudioPlayback lane) : IAudioPlayback, IAudioGainControl
    {
        private readonly AsyncDisposal disposal = new();

        public PcmAudioFormat Format => lane.Format;

        public double Gain
        {
            get => ((IAudioGainControl)lane).Gain;
            set => ((IAudioGainControl)lane).Gain = value;
        }

        public ValueTask WriteAsync(
            ReadOnlyMemory<short> samples,
            CancellationToken cancellationToken = default)
            => lane.WriteAsync(samples, cancellationToken);

        public ValueTask FlushAsync(CancellationToken cancellationToken = default)
            => lane.FlushAsync(cancellationToken);

        public ValueTask<int?> DrainAsync(CancellationToken cancellationToken = default)
            => lane.DrainAsync(cancellationToken);

        public ValueTask DisposeAsync()
            => disposal.RunAsync(DisposeCoreAsync);

        private async Task DisposeCoreAsync()
        {
            try
            {
                await lane.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await owner.ReleaseAsync(key, route).ConfigureAwait(false);
            }
        }
    }
}
