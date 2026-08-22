using DvmConsole.Audio;
using System.Diagnostics;

namespace DvmConsole.Desktop;

public sealed record MicrophoneReadinessTiming(
    TimeSpan CaptureStartReturned,
    TimeSpan FirstSamplesReceived,
    TimeSpan SustainedReadinessReached,
    long RequiredSamples);

// Fans one microphone capture stream out to independently-owned transmit
// calls. Each lease has the normal <see cref="IAudioCapture"/> lifecycle,
// while the physical device starts once for the first lease and stops after
// the final lease. This permits intentional multi-TX without competing for
// the microphone.
internal sealed class SharedAudioCapture : IAsyncDisposable
{
    private readonly IAudioCapture source;
    private readonly object sync = new();
    private readonly object publicationSync = new();
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly List<Lease> leases = [];
    private Lease[] runningLeases = [];
    private readonly long requiredReadinessSamples;
    private TaskCompletionSource<bool> samplesReady = CreateReadinessSource();
    private long observedReadinessSamples;
    private long readinessStartedTimestamp;
    private long captureStartCompletedTimestamp;
    private long firstSamplesTimestamp;
    private long readinessCompletedTimestamp;
    private bool samplesSuppressed;
    private bool disposed;

    public SharedAudioCapture(
        IAudioCapture source,
        TimeSpan? minimumReadinessDuration = null)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        TimeSpan duration = minimumReadinessDuration ?? TimeSpan.Zero;
        if (duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumReadinessDuration));
        requiredReadinessSamples = Math.Max(
            1,
            checked((long)Math.Ceiling(
                source.Format.SampleRate *
                source.Format.Channels *
                duration.TotalSeconds)));
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

    public bool IsReady
    {
        get
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return samplesReady.Task.IsCompletedSuccessfully;
            }
        }
    }

    public void SetSamplesSuppressed(bool suppressed)
    {
        lock (publicationSync)
        {
            lock (sync)
                ObjectDisposedException.ThrowIf(disposed, this);
            samplesSuppressed = suppressed;
        }
    }

    public async Task<MicrophoneReadinessTiming> WaitForSamplesAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        Task ready;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ready = samplesReady.Task;
        }

        await ready.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        lock (sync)
        {
            long started = readinessStartedTimestamp;
            if (started == 0 || firstSamplesTimestamp == 0 || readinessCompletedTimestamp == 0)
                throw new InvalidOperationException("Microphone readiness completed without timing checkpoints.");
            long captureStarted = captureStartCompletedTimestamp == 0
                ? firstSamplesTimestamp
                : captureStartCompletedTimestamp;
            return new MicrophoneReadinessTiming(
                Stopwatch.GetElapsedTime(started, captureStarted),
                Stopwatch.GetElapsedTime(started, firstSamplesTimestamp),
                Stopwatch.GetElapsedTime(started, readinessCompletedTimestamp),
                requiredReadinessSamples);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Lease[] current;
            lock (publicationSync)
            {
                lock (sync)
                {
                    if (disposed)
                        return;
                    disposed = true;
                    current = leases.ToArray();
                    leases.Clear();
                    runningLeases = [];
                }
                source.SamplesAvailable -= HandleSamplesAvailable;
            }

            foreach (Lease lease in current)
                lease.MarkDisposed();
            samplesReady.TrySetCanceled();
            await source.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async ValueTask StartAsync(Lease lease, CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool startSource;
            lock (publicationSync)
            {
                lock (sync)
                {
                    ThrowIfUnavailable(lease);
                    if (lease.IsRunning)
                        return;
                    startSource = runningLeases.Length == 0;
                    if (startSource)
                    {
                        samplesReady = CreateReadinessSource();
                        observedReadinessSamples = 0;
                        readinessStartedTimestamp = Stopwatch.GetTimestamp();
                        captureStartCompletedTimestamp = 0;
                        firstSamplesTimestamp = 0;
                        readinessCompletedTimestamp = 0;
                    }
                    lease.SetRunning(true);
                    PublishRunningLeasesLocked();
                }
            }

            if (!startSource)
                return;

            try
            {
                await source.StartAsync(cancellationToken).ConfigureAwait(false);
                lock (sync)
                    captureStartCompletedTimestamp = Stopwatch.GetTimestamp();
            }
            catch (Exception exception)
            {
                lock (publicationSync)
                {
                    lock (sync)
                    {
                        lease.SetRunning(false);
                        PublishRunningLeasesLocked();
                        samplesReady.TrySetException(exception);
                    }
                }
                throw;
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async ValueTask StopAsync(Lease lease, CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(lease, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async ValueTask DisposeLeaseAsync(Lease lease)
    {
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync(lease, CancellationToken.None).ConfigureAwait(false);
            lock (publicationSync)
            {
                lock (sync)
                {
                    leases.Remove(lease);
                    lease.MarkDisposed();
                    PublishRunningLeasesLocked();
                }
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async ValueTask StopCoreAsync(Lease lease, CancellationToken cancellationToken)
    {
        bool stopSource;
        lock (publicationSync)
        {
            lock (sync)
            {
                if (!leases.Contains(lease) || !lease.IsRunning)
                    return;
                lease.SetRunning(false);
                PublishRunningLeasesLocked();
                stopSource = runningLeases.Length == 0;
            }
        }

        if (stopSource)
            await source.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private void HandleSamplesAvailable(object? sender, PcmSamplesEventArgs args)
    {
        if (args.Samples.IsEmpty)
            return;

        lock (sync)
        {
            // Count physical capture before honoring suppression. A cold
            // Bluetooth endpoint can produce one callback while its duplex
            // profile is still changing, so transmit readiness can require a
            // sustained interval rather than treating that callback as proof
            // that the route has settled.
            if (firstSamplesTimestamp == 0)
                firstSamplesTimestamp = Stopwatch.GetTimestamp();
            observedReadinessSamples = Math.Min(
                requiredReadinessSamples,
                observedReadinessSamples + args.Samples.Length);
            if (observedReadinessSamples >= requiredReadinessSamples)
            {
                if (readinessCompletedTimestamp == 0)
                    readinessCompletedTimestamp = Stopwatch.GetTimestamp();
                samplesReady.TrySetResult(true);
            }
        }

        // Keep suppression and lifecycle changes serialized with publication.
        // Subscriber code runs outside the state lock, so a reentrant stop
        // cannot deadlock the capture callback.
        lock (publicationSync)
        {
            if (samplesSuppressed)
                return;
            foreach (Lease lease in runningLeases)
                lease.Publish(args);
        }
    }

    // Called with publicationSync and sync held. Lease transitions are rare;
    // callbacks reuse this immutable array without per-frame LINQ or copying.
    private void PublishRunningLeasesLocked()
        => runningLeases = leases.Where(candidate => candidate.IsRunning).ToArray();

    private void ThrowIfUnavailable(Lease lease)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!leases.Contains(lease) || lease.IsDisposed)
            throw new ObjectDisposedException(nameof(Lease));
    }

    private static TaskCompletionSource<bool> CreateReadinessSource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

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
