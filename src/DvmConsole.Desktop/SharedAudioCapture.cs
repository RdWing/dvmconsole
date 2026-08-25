using DvmConsole.Audio;
using DvmConsole.Operations;

namespace DvmConsole.Desktop;

public sealed record MicrophoneReadinessTiming(
    TimeSpan CaptureStartReturned,
    TimeSpan FirstSamplesReceived);

// Fans one microphone capture stream out to independently-owned transmit
// calls. Each lease has the normal <see cref="IAudioCapture"/> lifecycle,
// while the physical device starts once for the first lease and stops after
// the final lease. This permits intentional multi-TX without competing for
// the microphone.
internal sealed class SharedAudioCapture : IAsyncDisposable
{
    private static readonly TimeSpan MaximumCadenceAwareStaleDuration = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CallbackCadenceMargin = TimeSpan.FromMilliseconds(50);

    private readonly IAudioCapture source;
    private readonly object sync = new();
    private readonly object publicationSync = new();
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly List<Lease> leases = [];
    private Lease[] runningLeases = [];
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan staleAfter;
    private TaskCompletionSource<bool> samplesReady = CreateReadinessSource();
    private TaskCompletionSource<bool> nextPhysicalSamples = CreatePhysicalSamplesSource();
    private long readinessStartedTimestamp;
    private long captureStartCompletedTimestamp;
    private long firstSamplesTimestamp;
    private long lastSamplesTimestamp;
    private long previousSamplesTimestamp;
    private TimeSpan? callbackCadence;
    private long captureGeneration;
    private string? captureFault;
    private bool samplesSuppressed;
    private bool disposed;

    public SharedAudioCapture(
        IAudioCapture source,
        TimeSpan? staleAfter = null,
        TimeProvider? timeProvider = null)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.staleAfter = staleAfter ?? TimeSpan.FromMilliseconds(250);
        if (this.staleAfter <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(staleAfter));
        this.timeProvider = timeProvider ?? TimeProvider.System;
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
                return GetHealthLocked(timeProvider.GetTimestamp()).State ==
                    MicrophoneHealthState.Ready;
            }
        }
    }

    public MicrophoneHealth Health
    {
        get
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return GetHealthLocked(timeProvider.GetTimestamp());
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
            EnsureFreshReadinessLocked();
            ready = samplesReady.Task;
        }

        await ready.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        lock (sync)
        {
            long started = readinessStartedTimestamp;
            if (started == 0 || firstSamplesTimestamp == 0)
                throw new InvalidOperationException("Microphone readiness completed without timing checkpoints.");
            long captureStarted = captureStartCompletedTimestamp == 0
                ? firstSamplesTimestamp
                : captureStartCompletedTimestamp;
            return new MicrophoneReadinessTiming(
                timeProvider.GetElapsedTime(started, captureStarted),
                timeProvider.GetElapsedTime(started, firstSamplesTimestamp));
        }
    }

    // Waits for a physical callback that occurs after this method begins. This
    // is intentionally distinct from readiness: CoreAudio may pause an
    // otherwise-ready Bluetooth input while the permit-tone output opens and
    // closes. Releasing operator audio requires proof that capture resumed
    // after that route transition, not merely before it.
    public async Task<TimeSpan> WaitForNextPhysicalSamplesAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        Task observed;
        long started;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (runningLeases.Length == 0)
                throw new InvalidOperationException("The microphone capture path is not running.");
            started = timeProvider.GetTimestamp();
            observed = nextPhysicalSamples.Task;
        }

        await observed.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        return timeProvider.GetElapsedTime(started, timeProvider.GetTimestamp());
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
            nextPhysicalSamples.TrySetCanceled();
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
                        readinessStartedTimestamp = timeProvider.GetTimestamp();
                        captureStartCompletedTimestamp = 0;
                        firstSamplesTimestamp = 0;
                        lastSamplesTimestamp = 0;
                        previousSamplesTimestamp = 0;
                        callbackCadence = null;
                        captureFault = null;
                        captureGeneration = checked(captureGeneration + 1);
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
                    captureStartCompletedTimestamp = timeProvider.GetTimestamp();
            }
            catch (Exception exception)
            {
                lock (publicationSync)
                {
                    lock (sync)
                    {
                        lease.SetRunning(false);
                        PublishRunningLeasesLocked();
                        captureFault = exception.Message;
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
            // Observe physical capture before honoring suppression. The first
            // non-empty callback proves that the selected input has started;
            // cold Bluetooth release still requires another callback after
            // the output transition completes.
            long now = timeProvider.GetTimestamp();
            if (firstSamplesTimestamp == 0)
            {
                firstSamplesTimestamp = now;
                samplesReady.TrySetResult(true);
            }
            if (previousSamplesTimestamp != 0)
                callbackCadence = timeProvider.GetElapsedTime(previousSamplesTimestamp, now);
            previousSamplesTimestamp = now;
            lastSamplesTimestamp = now;
            captureFault = null;
            TaskCompletionSource<bool> observedSamples = nextPhysicalSamples;
            nextPhysicalSamples = CreatePhysicalSamplesSource();
            observedSamples.TrySetResult(true);
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

    private static TaskCompletionSource<bool> CreatePhysicalSamplesSource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void EnsureFreshReadinessLocked()
    {
        if (!samplesReady.Task.IsCompletedSuccessfully ||
            GetHealthLocked(timeProvider.GetTimestamp()).State == MicrophoneHealthState.Ready)
        {
            return;
        }

        samplesReady = CreateReadinessSource();
        readinessStartedTimestamp = timeProvider.GetTimestamp();
        captureStartCompletedTimestamp = readinessStartedTimestamp;
        firstSamplesTimestamp = 0;
    }

    private MicrophoneHealth GetHealthLocked(long now)
    {
        TimeSpan? sampleAge = lastSamplesTimestamp == 0
            ? null
            : timeProvider.GetElapsedTime(lastSamplesTimestamp, now);
        MicrophoneHealthState state;
        string? fault = captureFault;
        if (runningLeases.Length == 0)
        {
            state = MicrophoneHealthState.Stopped;
        }
        else if (!source.IsRunning)
        {
            state = MicrophoneHealthState.Faulted;
            fault ??= "Capture pump is not running.";
        }
        else if (!string.IsNullOrWhiteSpace(fault))
        {
            state = MicrophoneHealthState.Faulted;
        }
        else if (!samplesReady.Task.IsCompletedSuccessfully)
        {
            state = MicrophoneHealthState.Starting;
        }
        else if (sampleAge is null || sampleAge > GetEffectiveStaleDurationLocked())
        {
            state = MicrophoneHealthState.Stale;
        }
        else
        {
            state = MicrophoneHealthState.Ready;
        }

        return new MicrophoneHealth(
            state,
            captureGeneration,
            sampleAge,
            callbackCadence,
            fault);
    }

    private TimeSpan GetEffectiveStaleDurationLocked()
    {
        if (callbackCadence is not TimeSpan cadence)
            return staleAfter;

        double adaptiveMilliseconds = Math.Min(
            MaximumCadenceAwareStaleDuration.TotalMilliseconds,
            cadence.TotalMilliseconds * 4 + CallbackCadenceMargin.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(Math.Max(
            staleAfter.TotalMilliseconds,
            adaptiveMilliseconds));
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
