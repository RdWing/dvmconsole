using DvmConsole.Audio;

namespace DvmConsole.Desktop;

// Fans one microphone capture stream out to independently-owned transmit
// calls. Each lease has the normal <see cref="IAudioCapture"/> lifecycle,
// while the physical device starts once for the first lease and stops after
// the final lease. This permits intentional multi-TX without competing for
// the microphone.
internal sealed class SharedAudioCapture : IAsyncDisposable
{
    private readonly IAudioCapture source;
    private readonly object sync = new();
    private readonly List<Lease> leases = [];
    private bool samplesSuppressed;
    private bool disposed;

    public SharedAudioCapture(IAudioCapture source)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        source.SamplesAvailable += HandleSamplesAvailable;
    }

    public Lease CreateLease()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var lease = new Lease(this, source.Format);
            leases.Add(lease);
            return lease;
        }
    }

    public void SetSamplesSuppressed(bool suppressed)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            samplesSuppressed = suppressed;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Lease[] current;
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            current = leases.ToArray();
            leases.Clear();
        }

        foreach (Lease lease in current)
            lease.MarkDisposed();
        source.SamplesAvailable -= HandleSamplesAvailable;
        await source.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask StartAsync(Lease lease, CancellationToken cancellationToken)
    {
        bool startSource;
        lock (sync)
        {
            ThrowIfUnavailable(lease);
            if (lease.IsRunning)
                return;
            startSource = !leases.Any(candidate => candidate.IsRunning);
            lease.SetRunning(true);
        }

        if (!startSource)
            return;

        try
        {
            await source.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (sync)
                lease.SetRunning(false);
            throw;
        }
    }

    private async ValueTask StopAsync(Lease lease, CancellationToken cancellationToken)
    {
        bool stopSource;
        lock (sync)
        {
            if (!leases.Contains(lease) || !lease.IsRunning)
                return;
            lease.SetRunning(false);
            stopSource = !leases.Any(candidate => candidate.IsRunning);
        }

        if (stopSource)
            await source.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask DisposeLeaseAsync(Lease lease)
    {
        await StopAsync(lease, CancellationToken.None).ConfigureAwait(false);
        lock (sync)
        {
            leases.Remove(lease);
            lease.MarkDisposed();
        }
    }

    private void HandleSamplesAvailable(object? sender, PcmSamplesEventArgs args)
    {
        lock (sync)
        {
            if (samplesSuppressed)
                return;

            // Keep suppression changes serialized with publication. Once
            // SetSamplesSuppressed(true) returns, no callback that began just
            // before it can publish a trailing microphone frame.
            foreach (Lease lease in leases.Where(lease => lease.IsRunning).ToArray())
                lease.Publish(args);
        }
    }

    private void ThrowIfUnavailable(Lease lease)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!leases.Contains(lease) || lease.IsDisposed)
            throw new ObjectDisposedException(nameof(Lease));
    }

    internal sealed class Lease : IAudioCapture
    {
        private readonly SharedAudioCapture owner;
        private bool disposed;
        private bool running;

        internal Lease(SharedAudioCapture owner, PcmAudioFormat format)
        {
            this.owner = owner;
            Format = format;
        }

        public event EventHandler<PcmSamplesEventArgs>? SamplesAvailable;
        public PcmAudioFormat Format { get; }
        public bool IsRunning => running;
        internal bool IsDisposed => disposed;

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
            => owner.StartAsync(this, cancellationToken);

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
            => owner.StopAsync(this, cancellationToken);

        public ValueTask DisposeAsync() => disposed
            ? ValueTask.CompletedTask
            : owner.DisposeLeaseAsync(this);

        internal void SetRunning(bool value) => running = value;
        internal void MarkDisposed()
        {
            running = false;
            disposed = true;
        }

        internal void Publish(PcmSamplesEventArgs args) => SamplesAvailable?.Invoke(this, args);
    }
}
