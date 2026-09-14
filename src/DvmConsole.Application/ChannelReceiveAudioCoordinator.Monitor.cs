// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;

namespace DvmConsole.Application;

public sealed partial class ChannelReceiveAudioCoordinator
{
    // Accessed under the coordinator gate. A lease retains its exact route,
    // so late disposal cannot retire a replacement using the same device ID.
    private readonly Dictionary<ReceiveAudioRoute, HashSet<MonitorOutput>> monitorOutputs = [];

    public async ValueTask<IAudioPlayback> OpenMonitorOutputAsync(string? deviceId = null,
        CancellationToken cancellationToken = default)
    {
        await recoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                IAudioBackend? backend = backendPort.CreateAudioBackend();
                ReceiveAudioRoute? created = null;
                try
                {
                    var route = GetOrCreateRoute(backend, deviceId, out created, out _);
                    if (created is null) backend.Dispose();
                    backend = null;
                    var output = new MonitorOutput(this, route, route.Mixer.OpenMonitorChannel("local monitor"));
                    if (!monitorOutputs.TryGetValue(route, out var leases)) monitorOutputs[route] = leases = [];
                    leases.Add(output);
                    return output;
                }
                catch
                {
                    if (created is not null)
                    {
                        routeRegistry.TryRemoveRoute(created.DeviceId, out _);
                        await DisposeRouteAsync(created).ConfigureAwait(false);
                    }
                    else backend?.Dispose();
                    throw;
                }
            }
            finally { gate.Release(); }
        }
        finally { recoveryGate.Release(); }
    }

    private bool HasMonitorOutputs(string deviceId)
        => monitorOutputs.Any(pair => string.Equals(pair.Key.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) && pair.Value.Count != 0);

    private async Task RetireMonitorOutputsAsync(IEnumerable<ReceiveAudioRoute> routes)
    {
        List<Exception>? failures = null;
        foreach (var route in routes)
        {
            if (!monitorOutputs.Remove(route, out var outputs)) continue;
            foreach (var output in outputs)
            {
                try { await output.RetireAsync().ConfigureAwait(false); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
            }
        }
        if (failures is not null) throw new AggregateException("Monitor output cleanup failed.", failures);
    }

    private async Task ReleaseMonitorOutputAsync(MonitorOutput output)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var route = output.Route;
            if (!monitorOutputs.TryGetValue(route, out var outputs) || !outputs.Remove(output)) return;
            if (outputs.Count != 0) return;
            monitorOutputs.Remove(route);
            if (!routeRegistry.HasSessionsForRoute(route.DeviceId) &&
                routeRegistry.TryGetRoute(route.DeviceId, out var current) && ReferenceEquals(current, route))
            {
                routeRegistry.TryRemoveRoute(route.DeviceId, out _);
                await DisposeRouteAsync(route).ConfigureAwait(false);
            }
        }
        finally { gate.Release(); }
    }

    private sealed class MonitorOutput(ChannelReceiveAudioCoordinator owner, ReceiveAudioRoute route, IAudioPlayback inner) : IAudioPlayback, IAudioGainControl, IPhysicalAudioOutputDiagnosticsSource
    {
        private readonly AsyncDisposal retirement = new();
        private readonly AsyncDisposal disposal = new();
        private int retired;
        public ReceiveAudioRoute Route => route;
        public PhysicalAudioOutputDiagnostics GetPhysicalOutputDiagnostics()
            => ((IPhysicalAudioOutputDiagnosticsSource)inner).GetPhysicalOutputDiagnostics();
        public PcmAudioFormat Format => inner.Format;
        public double Gain
        {
            get => ((IAudioGainControl)inner).Gain;
            set => ((IAudioGainControl)inner).Gain = value;
        }
        public int? QueuedSamples => Volatile.Read(ref retired) != 0 ? 0 : inner.QueuedSamples;
        public ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref retired) != 0, this);
            return inner.WriteAsync(samples, cancellationToken);
        }
        public ValueTask FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);
        public ValueTask<int?> DrainAsync(CancellationToken cancellationToken = default) => inner.DrainAsync(cancellationToken);
        public ValueTask RetireAsync() => retirement.RunAsync(async () =>
        {
            Interlocked.Exchange(ref retired, 1);
            try { await inner.FlushAsync().ConfigureAwait(false); }
            finally { await inner.DisposeAsync().ConfigureAwait(false); }
        });
        public ValueTask DisposeAsync() => disposal.RunAsync(async () =>
        {
            try { await RetireAsync().ConfigureAwait(false); }
            finally { await owner.ReleaseMonitorOutputAsync(this).ConfigureAwait(false); }
        });
    }
}
