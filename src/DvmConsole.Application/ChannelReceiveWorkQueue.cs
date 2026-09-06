// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;
using DvmConsole.Operations;

namespace DvmConsole.Application;

internal readonly record struct RadioMediaIngressFrame
{
    public RadioMediaIngressFrame(
        IRadioMediaFrame traffic,
        long boundaryTimestamp,
        long transportIngressTimestamp = 0,
        RadioFrameEncryption? encryption = null)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        if (boundaryTimestamp <= 0)
            throw new ArgumentOutOfRangeException(nameof(boundaryTimestamp));
        if (transportIngressTimestamp < 0 || transportIngressTimestamp > boundaryTimestamp)
            throw new ArgumentOutOfRangeException(nameof(transportIngressTimestamp));

        Traffic = traffic;
        BoundaryTimestamp = boundaryTimestamp;
        TransportIngressTimestamp = transportIngressTimestamp;
        Encryption = encryption;
    }

    public IRadioMediaFrame Traffic { get; }
    public long BoundaryTimestamp { get; }
    public long TransportIngressTimestamp { get; }
    public RadioFrameEncryption? Encryption { get; }
}

internal readonly record struct ReceiveWorkQueueDiagnostics(
    long ProcessedFrames,
    TimeSpan MaximumInterArrivalDelay,
    TimeSpan MaximumIngressToQueueDelay,
    TimeSpan MaximumQueueDelay,
    TimeSpan MaximumProcessingDuration,
    TimeSpan MaximumEndToEndDelay,
    TimeSpan MaximumTransportInterArrivalDelay = default,
    TimeSpan MaximumTransportToApplicationBoundaryDelay = default,
    TimeSpan MaximumJitterBufferTargetDelay = default,
    long JitterBufferReorderedPackets = 0,
    long JitterBufferDeadlineMissedPackets = 0,
    long WakeSignals = 0,
    long CoalescedWakeSignals = 0,
    long WakeWaits = 0,
    long WakeTimeouts = 0,
    int PeakPendingFrames = 0,
    long SpuriousWakeSignals = 0,
    TimeSpan MaximumJitterBufferHoldDuration = default,
    TimeSpan MaximumWorkerBacklogDuration = default,
    TimeSpan MaximumSessionGateDelay = default,
    TimeSpan MaximumSessionProcessingDuration = default,
    long WorkerStarts = 0,
    TimeSpan MaximumOrderedDrainHoldDuration = default);

internal readonly record struct ReceiveWorkItemTiming(
    IRadioMediaFrame Traffic,
    TimeSpan InterArrivalDelay,
    TimeSpan IngressToQueueDelay,
    TimeSpan QueueDelay,
    TimeSpan ProcessingDuration,
    TimeSpan EndToEndDelay,
    TimeSpan TransportInterArrivalDelay = default,
    TimeSpan TransportToApplicationBoundaryDelay = default,
    TimeSpan JitterBufferTargetDelay = default,
    bool AdaptiveJitterBuffer = false,
    bool JitterBufferReorderedPacket = false,
    int JitterBufferDeadlineMissedPackets = 0,
    TimeSpan JitterBufferHoldDuration = default,
    TimeSpan WorkerBacklogDuration = default,
    TimeSpan SessionGateDelay = default,
    TimeSpan SessionProcessingDuration = default,
    bool? EncryptedSessionProcessing = null,
    bool HasQueueDelayBreakdown = false,
    bool HasSessionProcessingBreakdown = false,
    TimeSpan OrderedDrainHoldDuration = default);

internal readonly record struct ReceiveProcessingStageTiming(
    TimeSpan SessionGateDelay,
    TimeSpan SessionProcessingDuration,
    bool? EncryptedSessionProcessing,
    bool HasSessionProcessingBreakdown = true);

internal enum ReceiveWorkerActivity
{
    Waiting,
    ProcessingFrame,
    RunningContinuation
}

internal readonly record struct ReceiveWorkerShutdownDiagnostic(
    ChannelId ChannelId,
    uint? StreamId,
    ReceiveWorkerActivity Activity,
    int PendingFrames,
    int PendingContinuations,
    bool CancellationAcknowledgementTimedOut = false);

// A state change only needs to wake the single channel worker once. Keeping at
// most one pending signal prevents a burst of already-processed frames from
// turning into stale, immediate wakeups at a later jitter-buffer deadline.
internal sealed class CoalescingWakeSignal : IDisposable
{
    private readonly SemaphoreSlim signal = new(0, 1);
    private int pending;

    public bool Set()
    {
        if (Interlocked.Exchange(ref pending, 1) != 0)
            return false;

        signal.Release();
        return true;
    }

    public async ValueTask<bool> WaitAsync(TimeSpan timeout)
    {
        bool signaled;
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            await signal.WaitAsync().ConfigureAwait(false);
            signaled = true;
        }
        else
        {
            signaled = await signal.WaitAsync(timeout).ConfigureAwait(false);
        }

        if (signaled)
        {
            // A Set racing this reset is still observed by the worker's state
            // recheck; a later Set publishes the next binary signal normally.
            Volatile.Write(ref pending, 0);
        }
        return signaled;
    }

    internal bool TryConsume()
    {
        if (!signal.Wait(0))
            return false;

        Volatile.Write(ref pending, 0);
        return true;
    }

    public void Dispose()
        => signal.Dispose();
}

// Keeps receive work ordered for one channel without coupling it to any other
// channel. The bounded pending buffer prevents a slow decoder or output device
// from growing an unbounded continuation chain during a busy period.
internal sealed class ChannelReceiveWorkQueue : IAsyncDisposable
{
    // Preserve ordered terminator and recording work briefly, then cancel any
    // decoder or episode drain that would otherwise consume the window's
    // ten-second shutdown ceiling. Cancellation should normally unwind within
    // the shorter acknowledgement interval.
    internal static readonly TimeSpan DefaultShutdownDrainTimeout = TimeSpan.FromMilliseconds(750);
    internal static readonly TimeSpan DefaultCancellationAcknowledgementTimeout = TimeSpan.FromMilliseconds(250);
    private readonly object sync = new();
    private readonly Dictionary<ChannelId, ChannelWorker> workers = [];
    private readonly HashSet<ChannelWorker> retiredWorkers = [];
    private readonly HashSet<Task> ownedContinuations = [];
    private readonly Dictionary<ChannelId, TimingAccumulator> timing = [];
    private readonly HashSet<ChannelId> stoppedChannels = [];
    private readonly Func<ChannelId, IRadioMediaFrame, CancellationToken, Task>? process;
    private readonly Func<ChannelId, IRadioMediaFrame, CancellationToken, Task<ReceiveProcessingStageTiming>>?
        processWithTiming;
    private readonly Func<ChannelId, RadioMediaIngressFrame, CancellationToken, Task<ReceiveProcessingStageTiming>>?
        processIngressWithTiming;
    private readonly Action<ChannelId, ReceiveWorkItemTiming>? timingObserver;
    private readonly Func<ChannelId, RadioMediaProtocol, ReceiveJitterBufferProfile> getJitterBufferProfile;
    private readonly IReceiveWorkQueueScheduler scheduler;
    private readonly Action<IReadOnlyList<ReceiveWorkerShutdownDiagnostic>>? shutdownDelayObserver;
    private readonly TimeSpan shutdownDrainTimeout;
    private readonly TimeSpan cancellationAcknowledgementTimeout;
    private readonly CancellationTokenSource shutdown = new();
    private readonly AsyncDisposal disposal = new();
    private readonly int maxPendingFramesPerChannel;
    private int currentPendingFrames;
    private int peakPendingFrames;
    private bool disposed;

    public ChannelReceiveWorkQueue(
        Func<ChannelId, IRadioMediaFrame, Task> process,
        int maxPendingFramesPerChannel = 64,
        Action<ChannelId, ReceiveWorkItemTiming>? timingObserver = null,
        Func<ChannelId, RadioMediaProtocol, ReceiveJitterBufferProfile>? getJitterBufferProfile = null,
        IReceiveWorkQueueScheduler? scheduler = null,
        Action<IReadOnlyList<ReceiveWorkerShutdownDiagnostic>>? shutdownDelayObserver = null,
        TimeSpan? shutdownDrainTimeout = null,
        TimeSpan? cancellationAcknowledgementTimeout = null)
        : this(
            process is null
                ? throw new ArgumentNullException(nameof(process))
                : (channelId, traffic, _) => process(channelId, traffic),
            processWithTiming: null,
            processIngressWithTiming: null,
            maxPendingFramesPerChannel,
            timingObserver,
            getJitterBufferProfile,
            scheduler,
            shutdownDelayObserver,
            shutdownDrainTimeout,
            cancellationAcknowledgementTimeout)
    {
    }

    public ChannelReceiveWorkQueue(
        Func<ChannelId, IRadioMediaFrame, CancellationToken, Task> process,
        int maxPendingFramesPerChannel = 64,
        Action<ChannelId, ReceiveWorkItemTiming>? timingObserver = null,
        Func<ChannelId, RadioMediaProtocol, ReceiveJitterBufferProfile>? getJitterBufferProfile = null,
        IReceiveWorkQueueScheduler? scheduler = null,
        Action<IReadOnlyList<ReceiveWorkerShutdownDiagnostic>>? shutdownDelayObserver = null,
        TimeSpan? shutdownDrainTimeout = null,
        TimeSpan? cancellationAcknowledgementTimeout = null)
        : this(
            process ?? throw new ArgumentNullException(nameof(process)),
            processWithTiming: null,
            processIngressWithTiming: null,
            maxPendingFramesPerChannel,
            timingObserver,
            getJitterBufferProfile,
            scheduler,
            shutdownDelayObserver,
            shutdownDrainTimeout,
            cancellationAcknowledgementTimeout)
    {
    }

    public static ChannelReceiveWorkQueue CreateWithTiming(
        Func<ChannelId, IRadioMediaFrame, Task<ReceiveProcessingStageTiming>> process,
        int maxPendingFramesPerChannel = 64,
        Action<ChannelId, ReceiveWorkItemTiming>? timingObserver = null,
        Func<ChannelId, RadioMediaProtocol, ReceiveJitterBufferProfile>? getJitterBufferProfile = null,
        IReceiveWorkQueueScheduler? scheduler = null,
        Action<IReadOnlyList<ReceiveWorkerShutdownDiagnostic>>? shutdownDelayObserver = null,
        TimeSpan? shutdownDrainTimeout = null,
        TimeSpan? cancellationAcknowledgementTimeout = null)
        => new(
            process: null,
            processWithTiming: process is null
                ? throw new ArgumentNullException(nameof(process))
                : (channelId, traffic, _) => process(channelId, traffic),
            processIngressWithTiming: null,
            maxPendingFramesPerChannel,
            timingObserver,
            getJitterBufferProfile,
            scheduler,
            shutdownDelayObserver,
            shutdownDrainTimeout,
            cancellationAcknowledgementTimeout);

    public static ChannelReceiveWorkQueue CreateWithIngressTiming(
        Func<ChannelId, RadioMediaIngressFrame, CancellationToken, Task<ReceiveProcessingStageTiming>> process,
        int maxPendingFramesPerChannel = 64,
        Action<ChannelId, ReceiveWorkItemTiming>? timingObserver = null,
        Func<ChannelId, RadioMediaProtocol, ReceiveJitterBufferProfile>? getJitterBufferProfile = null,
        IReceiveWorkQueueScheduler? scheduler = null,
        Action<IReadOnlyList<ReceiveWorkerShutdownDiagnostic>>? shutdownDelayObserver = null,
        TimeSpan? shutdownDrainTimeout = null,
        TimeSpan? cancellationAcknowledgementTimeout = null)
        => new(
            process: null,
            processWithTiming: null,
            processIngressWithTiming: process ?? throw new ArgumentNullException(nameof(process)),
            maxPendingFramesPerChannel,
            timingObserver,
            getJitterBufferProfile,
            scheduler,
            shutdownDelayObserver,
            shutdownDrainTimeout,
            cancellationAcknowledgementTimeout);

    public static ChannelReceiveWorkQueue CreateWithTiming(
        Func<ChannelId, IRadioMediaFrame, CancellationToken, Task<ReceiveProcessingStageTiming>> process,
        int maxPendingFramesPerChannel = 64,
        Action<ChannelId, ReceiveWorkItemTiming>? timingObserver = null,
        Func<ChannelId, RadioMediaProtocol, ReceiveJitterBufferProfile>? getJitterBufferProfile = null,
        IReceiveWorkQueueScheduler? scheduler = null,
        Action<IReadOnlyList<ReceiveWorkerShutdownDiagnostic>>? shutdownDelayObserver = null,
        TimeSpan? shutdownDrainTimeout = null,
        TimeSpan? cancellationAcknowledgementTimeout = null)
        => new(
            process: null,
            processWithTiming: process ?? throw new ArgumentNullException(nameof(process)),
            processIngressWithTiming: null,
            maxPendingFramesPerChannel,
            timingObserver,
            getJitterBufferProfile,
            scheduler,
            shutdownDelayObserver,
            shutdownDrainTimeout,
            cancellationAcknowledgementTimeout);

    private ChannelReceiveWorkQueue(
        Func<ChannelId, IRadioMediaFrame, CancellationToken, Task>? process,
        Func<ChannelId, IRadioMediaFrame, CancellationToken, Task<ReceiveProcessingStageTiming>>? processWithTiming,
        Func<ChannelId, RadioMediaIngressFrame, CancellationToken, Task<ReceiveProcessingStageTiming>>?
            processIngressWithTiming,
        int maxPendingFramesPerChannel,
        Action<ChannelId, ReceiveWorkItemTiming>? timingObserver,
        Func<ChannelId, RadioMediaProtocol, ReceiveJitterBufferProfile>? getJitterBufferProfile,
        IReceiveWorkQueueScheduler? scheduler,
        Action<IReadOnlyList<ReceiveWorkerShutdownDiagnostic>>? shutdownDelayObserver,
        TimeSpan? shutdownDrainTimeout,
        TimeSpan? cancellationAcknowledgementTimeout)
    {
        this.process = process;
        this.processWithTiming = processWithTiming;
        this.processIngressWithTiming = processIngressWithTiming;
        if (maxPendingFramesPerChannel < 1)
            throw new ArgumentOutOfRangeException(nameof(maxPendingFramesPerChannel));
        this.maxPendingFramesPerChannel = maxPendingFramesPerChannel;
        this.timingObserver = timingObserver;
        this.getJitterBufferProfile = getJitterBufferProfile ?? ((_, _) => default);
        this.scheduler = scheduler ?? SystemReceiveWorkQueueScheduler.Instance;
        this.shutdownDelayObserver = shutdownDelayObserver;
        this.shutdownDrainTimeout = shutdownDrainTimeout ?? DefaultShutdownDrainTimeout;
        if (this.shutdownDrainTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(shutdownDrainTimeout));
        this.cancellationAcknowledgementTimeout = cancellationAcknowledgementTimeout ??
            DefaultCancellationAcknowledgementTimeout;
        if (this.cancellationAcknowledgementTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cancellationAcknowledgementTimeout));
    }

    public void Start(ChannelId channelId)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            stoppedChannels.Remove(channelId);
            if (!workers.ContainsKey(channelId))
                timing[channelId] = new TimingAccumulator(scheduler);
        }
    }

    public ReceiveWorkQueueDiagnostics GetDiagnostics(
        ChannelId channelId,
        uint? streamId = null)
    {
        lock (sync)
            return timing.TryGetValue(channelId, out TimingAccumulator? current)
                ? current.Snapshot(streamId)
                : default;
    }

    public ReceiveQueueHealth CaptureHealth()
    {
        lock (sync)
        {
            long coalescedWakeCount = 0;
            long spuriousWakeCount = 0;
            foreach (TimingAccumulator accumulator in timing.Values)
            {
                ReceiveWorkQueueDiagnostics diagnostics = accumulator.Snapshot(streamId: null);
                coalescedWakeCount = SaturatingAdd(
                    coalescedWakeCount,
                    diagnostics.CoalescedWakeSignals);
                spuriousWakeCount = SaturatingAdd(
                    spuriousWakeCount,
                    diagnostics.SpuriousWakeSignals);
            }

            return new ReceiveQueueHealth(
                Volatile.Read(ref currentPendingFrames),
                Volatile.Read(ref peakPendingFrames),
                coalescedWakeCount,
                spuriousWakeCount);
        }
    }

    public bool Enqueue(ChannelId channelId, RadioMediaIngressFrame ingress)
        => Enqueue(channelId, ingress, out _);

    public bool Enqueue(
        ChannelId channelId,
        RadioMediaIngressFrame ingress,
        out bool droppedFrame)
    {
        lock (sync)
        {
            if (disposed || stoppedChannels.Contains(channelId))
            {
                droppedFrame = true;
                return false;
            }

            if (!workers.TryGetValue(channelId, out ChannelWorker? worker))
            {
                if (!timing.TryGetValue(channelId, out TimingAccumulator? accumulator))
                {
                    accumulator = new TimingAccumulator(scheduler);
                    timing.Add(channelId, accumulator);
                }
                worker = new ChannelWorker(
                    channelId,
                    process,
                    processWithTiming,
                    processIngressWithTiming,
                    accumulator,
                    maxPendingFramesPerChannel,
                    timingObserver,
                    getJitterBufferProfile,
                    scheduler,
                    ObservePendingDepthChange,
                    shutdown.Token);
                workers.Add(channelId, worker);
            }

            return worker.Enqueue(ingress, out droppedFrame);
        }
    }

    public async Task StopAsync(ChannelId channelId)
    {
        ChannelWorker? worker;
        lock (sync)
        {
            stoppedChannels.Add(channelId);
            workers.Remove(channelId, out worker);
            if (worker is not null)
                retiredWorkers.Add(worker);
        }

        if (worker is not null)
        {
            worker.Complete();
            if (!await WaitForCompletionAsync(worker.Completion, shutdownDrainTimeout)
                    .ConfigureAwait(false))
            {
                ReportShutdownDelay([worker], cancellationAcknowledgementTimedOut: false);
                worker.Cancel();
                if (!await WaitForCompletionAsync(
                        worker.Completion,
                        cancellationAcknowledgementTimeout).ConfigureAwait(false))
                {
                    ReceiveWorkerShutdownDiagnostic diagnostic =
                        worker.CaptureShutdownDiagnostic(
                            cancellationAcknowledgementTimedOut: true);
                    NotifyShutdownDelay([diagnostic]);
                    ObserveBackground(DisposeRetiredWorkerWhenCompleteAsync(worker));
                    throw new TimeoutException(CreateShutdownTimeoutMessage([diagnostic]));
                }
            }

            try
            {
                await worker.Completion.ConfigureAwait(false);
            }
            finally
            {
                bool ownsDisposal;
                lock (sync)
                    ownsDisposal = retiredWorkers.Remove(worker);
                if (ownsDisposal)
                    worker.Dispose();
            }
        }
    }

    private async Task DisposeRetiredWorkerWhenCompleteAsync(ChannelWorker worker)
    {
        try
        {
            await worker.Completion.ConfigureAwait(false);
        }
        catch
        {
            // The StopAsync caller received the timeout. Completion remains
            // observed here solely so the retired worker can release its
            // native-adjacent lifetime after it finally exits.
        }
        finally
        {
            bool ownsDisposal;
            lock (sync)
                ownsDisposal = retiredWorkers.Remove(worker);
            if (ownsDisposal)
                worker.Dispose();
        }
    }

    // Runs lifecycle cleanup on the channel worker after every packet already
    // buffered for this stream has passed through the ordered processor. The
    // queue owns ordering only; audio and recording cleanup remain callers'
    // responsibilities.
    public Task RunAfterStreamAsync(
        ChannelId channelId,
        uint streamId,
        Func<Task> continuation)
        => RunAfterStreamsAsync(channelId, [streamId], continuation);

    public Task RunAfterStreamAsync(
        ChannelId channelId,
        uint streamId,
        Func<CancellationToken, Task> continuation)
        => RunAfterStreamsAsync(channelId, [streamId], continuation);

    // Runs one episode-level continuation after every buffered packet for any
    // physical stream in the episode has passed through the channel worker.
    // Unrelated streams retain their own jitter deadlines and do not delay it.
    public Task RunAfterStreamsAsync(
        ChannelId channelId,
        IReadOnlyCollection<uint> streamIds,
        Func<Task> continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        return RunAfterStreamsAsync(channelId, streamIds, _ => continuation());
    }

    public Task RunAfterStreamsAsync(
        ChannelId channelId,
        IReadOnlyCollection<uint> streamIds,
        Func<CancellationToken, Task> continuation)
    {
        ArgumentNullException.ThrowIfNull(streamIds);
        uint[] normalizedStreamIds = streamIds.Distinct().ToArray();
        if (normalizedStreamIds.Length == 0 || normalizedStreamIds.Any(streamId => streamId == 0))
            throw new ArgumentException("At least one non-zero stream ID is required.", nameof(streamIds));
        ArgumentNullException.ThrowIfNull(continuation);

        ChannelWorker? worker;
        TaskCompletionSource? directCompletion = null;
        Task? ownedContinuation = null;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            workers.TryGetValue(channelId, out worker);
            if (worker is null)
            {
                directCompletion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                ownedContinuation = directCompletion.Task;
                ownedContinuations.Add(ownedContinuation);
            }
        }

        if (worker is not null)
            return worker.RunAfterStreamsAsync(normalizedStreamIds, continuation);

        _ = Task.Run(() => ExecuteOwnedContinuationAsync(directCompletion!, continuation));
        _ = ownedContinuation!.ContinueWith(
            completed =>
            {
                lock (sync)
                    ownedContinuations.Remove(completed);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return ownedContinuation;
    }

    public ValueTask DisposeAsync()
        => disposal.RunAsync(DisposeCoreAsync);

    private async Task DisposeCoreAsync()
    {
        ChannelWorker[] oldWorkers;
        Task[] directContinuations;
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            oldWorkers = workers.Values.Concat(retiredWorkers).Distinct().ToArray();
            directContinuations = ownedContinuations.ToArray();
            workers.Clear();
            retiredWorkers.Clear();
            ownedContinuations.Clear();
        }

        foreach (ChannelWorker worker in oldWorkers)
            worker.Complete(discardPendingVoice: true);
        Task completion = Task.WhenAll(
            oldWorkers.Select(worker => worker.Completion).Concat(directContinuations));
        if (await WaitForCompletionAsync(completion, shutdownDrainTimeout).ConfigureAwait(false))
        {
            await FinishDisposalAsync(completion, oldWorkers, shutdown).ConfigureAwait(false);
            return;
        }

        ReportShutdownDelay(oldWorkers, cancellationAcknowledgementTimedOut: false);
        shutdown.Cancel();
        foreach (ChannelWorker worker in oldWorkers)
            worker.Cancel();

        if (await WaitForCompletionAsync(completion, cancellationAcknowledgementTimeout)
                .ConfigureAwait(false))
        {
            await FinishDisposalAsync(completion, oldWorkers, shutdown).ConfigureAwait(false);
            return;
        }

        ReceiveWorkerShutdownDiagnostic[] stalled = oldWorkers
            .Where(worker => !worker.Completion.IsCompleted)
            .Select(worker => worker.CaptureShutdownDiagnostic(
                cancellationAcknowledgementTimedOut: true))
            .ToArray();
        NotifyShutdownDelay(stalled);
        ObserveBackground(DisposeWorkersWhenCompleteAsync(completion, oldWorkers, shutdown));
        throw new TimeoutException(CreateShutdownTimeoutMessage(stalled));
    }

    private void ReportShutdownDelay(
        IEnumerable<ChannelWorker> workers,
        bool cancellationAcknowledgementTimedOut)
        => NotifyShutdownDelay(workers
            .Where(worker => !worker.Completion.IsCompleted)
            .Select(worker => worker.CaptureShutdownDiagnostic(cancellationAcknowledgementTimedOut))
            .ToArray());

    private void NotifyShutdownDelay(IReadOnlyList<ReceiveWorkerShutdownDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
            return;
        try
        {
            shutdownDelayObserver?.Invoke(diagnostics);
        }
        catch
        {
            // Shutdown diagnostics must never prevent cancellation or cleanup.
        }
    }

    private async Task<bool> WaitForCompletionAsync(Task completion, TimeSpan timeout)
    {
        Task winner = await Task.WhenAny(
            completion,
            scheduler.DelayAsync(timeout)).ConfigureAwait(false);
        return ReferenceEquals(winner, completion);
    }

    private async Task ExecuteOwnedContinuationAsync(
        TaskCompletionSource completion,
        Func<CancellationToken, Task> continuation)
    {
        try
        {
            await continuation(shutdown.Token).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (OperationCanceledException exception)
        {
            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private static void DisposeWorkers(IEnumerable<ChannelWorker> workers)
    {
        foreach (ChannelWorker worker in workers)
            worker.Dispose();
    }

    private static async Task DisposeWorkersWhenCompleteAsync(
        Task completion,
        ChannelWorker[] workers,
        CancellationTokenSource shutdown)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        finally
        {
            DisposeWorkers(workers);
            shutdown.Dispose();
        }
    }

    private static async Task FinishDisposalAsync(
        Task completion,
        ChannelWorker[] workers,
        CancellationTokenSource shutdown)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        finally
        {
            DisposeWorkers(workers);
            shutdown.Dispose();
        }
    }

    private static string CreateShutdownTimeoutMessage(
        IReadOnlyList<ReceiveWorkerShutdownDiagnostic> diagnostics)
        => "Receive workers did not acknowledge shutdown: " + string.Join(
            "; ",
            diagnostics.Select(diagnostic =>
                $"channel={diagnostic.ChannelId}, stream={diagnostic.StreamId?.ToString() ?? "none"}, " +
                $"activity={diagnostic.Activity}, pendingFrames={diagnostic.PendingFrames}, " +
                $"pendingContinuations={diagnostic.PendingContinuations}"));

    private sealed class ChannelWorker
    {
        private readonly object sync = new();
        private readonly ReceivePacketJitterBuffer<WorkItem> pending;
        private readonly CoalescingWakeSignal wakeSignal = new();
        private readonly ChannelId channelId;
        private readonly Func<ChannelId, IRadioMediaFrame, CancellationToken, Task>? process;
        private readonly Func<ChannelId, IRadioMediaFrame, CancellationToken, Task<ReceiveProcessingStageTiming>>?
            processWithTiming;
        private readonly Func<ChannelId, RadioMediaIngressFrame, CancellationToken, Task<ReceiveProcessingStageTiming>>?
            processIngressWithTiming;
        private readonly TimingAccumulator timing;
        private readonly int maxPendingFrames;
        private readonly Action<ChannelId, ReceiveWorkItemTiming>? timingObserver;
        private readonly Func<ChannelId, RadioMediaProtocol, ReceiveJitterBufferProfile> getJitterBufferProfile;
        private readonly IReceiveWorkQueueScheduler scheduler;
        private readonly Action<int> pendingDepthChanged;
        private readonly CancellationTokenSource lifetime;
        private readonly TaskCompletionSource completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<StreamContinuation> streamContinuations = [];
        private uint? processingStreamId;
        private uint? continuationStreamId;
        private ReceiveWorkerActivity activity = ReceiveWorkerActivity.Waiting;
        private bool accepting = true;
        private bool running;

        public ChannelWorker(
            ChannelId channelId,
            Func<ChannelId, IRadioMediaFrame, CancellationToken, Task>? process,
            Func<ChannelId, IRadioMediaFrame, CancellationToken, Task<ReceiveProcessingStageTiming>>? processWithTiming,
            Func<ChannelId, RadioMediaIngressFrame, CancellationToken, Task<ReceiveProcessingStageTiming>>?
                processIngressWithTiming,
            TimingAccumulator timing,
            int maxPendingFrames,
            Action<ChannelId, ReceiveWorkItemTiming>? timingObserver,
            Func<ChannelId, RadioMediaProtocol, ReceiveJitterBufferProfile> getJitterBufferProfile,
            IReceiveWorkQueueScheduler scheduler,
            Action<int> pendingDepthChanged,
            CancellationToken shutdownToken)
        {
            this.channelId = channelId;
            this.process = process;
            this.processWithTiming = processWithTiming;
            this.processIngressWithTiming = processIngressWithTiming;
            this.timing = timing;
            this.maxPendingFrames = maxPendingFrames;
            this.timingObserver = timingObserver;
            this.getJitterBufferProfile = getJitterBufferProfile;
            this.scheduler = scheduler;
            this.pendingDepthChanged = pendingDepthChanged;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
            pending = new ReceivePacketJitterBuffer<WorkItem>(
                item => item.Traffic.StreamId,
                item => item.Traffic.PacketSequence,
                item => RadioReceiveTrafficClassifier.GetJitterPacketKind(item.Traffic),
                item => item.JitterBufferProfile,
                scheduler);
        }

        public Task Completion => completion.Task;

        public int PendingCount
        {
            get
            {
                lock (sync)
                    return pending.Count;
            }
        }

        public bool Enqueue(
            RadioMediaIngressFrame ingress,
            out bool droppedFrame)
        {
            IRadioMediaFrame traffic = ingress.Traffic;
            lock (sync)
            {
                if (!accepting)
                {
                    droppedFrame = true;
                    return false;
                }

                int previousPendingCount = pending.Count;
                droppedFrame = previousPendingCount >= maxPendingFrames;
                if (droppedFrame && !MakeRoomFor(traffic))
                    return false;

                ReceiveIngressTiming ingressTiming = timing.ObserveIngress(
                    traffic.StreamId,
                    ingress.TransportIngressTimestamp,
                    ingress.BoundaryTimestamp);
                long enqueuedTimestamp = scheduler.GetTimestamp();
                pending.Enqueue(new WorkItem(
                    ingress,
                    ingressTiming.ApplicationInterArrivalDelay,
                    ingressTiming.TransportInterArrivalDelay,
                    ingressTiming.TransportToApplicationBoundaryDelay,
                    enqueuedTimestamp,
                    getJitterBufferProfile(channelId, traffic.Protocol)), enqueuedTimestamp);
                pendingDepthChanged(pending.Count - previousPendingCount);
                SignalWake();
                if (!running)
                {
                    running = true;
                    timing.RecordWorkerStart();
                    ObserveBackground(Task.Run(ProcessLoopAsync));
                }
                return true;
            }
        }

        public void Complete(bool discardPendingVoice = false)
        {
            lock (sync)
            {
                accepting = false;
                if (discardPendingVoice)
                {
                    int discarded = 0;
                    while (pending.TryRemoveOldest(item =>
                               RadioReceiveTrafficClassifier.GetJitterPacketKind(item.Traffic) ==
                               ReceiveJitterPacketKind.Voice))
                    {
                        discarded++;
                    }
                    if (discarded > 0)
                        pendingDepthChanged(-discarded);
                }
                SignalWake();
                if (!running)
                    completion.TrySetResult();
            }
        }

        public void Cancel()
        {
            lock (sync)
            {
                accepting = false;
                int discarded = 0;
                while (pending.TryRemoveOldest(_ => true))
                    discarded++;
                if (discarded > 0)
                    pendingDepthChanged(-discarded);
                foreach (StreamContinuation continuation in streamContinuations)
                    continuation.Completion.TrySetCanceled(lifetime.Token);
                streamContinuations.Clear();
                SignalWake();
                if (!running)
                    completion.TrySetResult();
            }
            lifetime.Cancel();
        }

        public ReceiveWorkerShutdownDiagnostic CaptureShutdownDiagnostic(
            bool cancellationAcknowledgementTimedOut)
        {
            lock (sync)
            {
                return new ReceiveWorkerShutdownDiagnostic(
                    channelId,
                    processingStreamId ?? continuationStreamId,
                    activity,
                    pending.Count,
                    streamContinuations.Count,
                    cancellationAcknowledgementTimedOut);
            }
        }

        public Task RunAfterStreamsAsync(
            IReadOnlyCollection<uint> streamIds,
            Func<CancellationToken, Task> continuation)
        {
            var request = new StreamContinuation(streamIds, continuation);
            lock (sync)
            {
                streamContinuations.Add(request);
                SignalWake();
                if (!running)
                {
                    running = true;
                    timing.RecordWorkerStart();
                    ObserveBackground(Task.Run(ProcessLoopAsync));
                }
            }
            return request.Completion.Task;
        }

        public void Dispose()
        {
            wakeSignal.Dispose();
            lifetime.Dispose();
        }

        private void SignalWake()
            => timing.RecordWakeRequest(wakeSignal.Set(), pending.Count);

        private bool MakeRoomFor(IRadioMediaFrame incoming)
        {
            if (pending.TryRemoveOldestSuperseded())
                return true;

            if (RadioReceiveTrafficClassifier.IsTerminator(incoming) &&
                pending.TryRemoveOldest(item =>
                    !RadioReceiveTrafficClassifier.IsTerminator(item.Traffic) &&
                    item.Traffic.StreamId != incoming.StreamId))
            {
                return true;
            }

            if (pending.TryRemoveOldest(item =>
                    RadioReceiveTrafficClassifier.GetJitterPacketKind(item.Traffic) ==
                    ReceiveJitterPacketKind.Voice))
                return true;

            if (!RadioReceiveTrafficClassifier.IsTerminator(incoming))
                return false;

            return pending.TryRemoveOldest(_ => true);
        }

        private async Task ProcessLoopAsync()
        {
            Exception? terminalFailure = null;
            try
            {
                while (true)
                {
                    lifetime.Token.ThrowIfCancellationRequested();
                    WorkItem item = default;
                    StreamContinuation? streamContinuation = null;
                    TimeSpan waitTime;
                    ReceiveJitterBufferDequeueMetadata jitterMetadata;
                    bool hasItem;
                    lock (sync)
                    {
                        if (TryTakeReadyStreamContinuation(out StreamContinuation? readyContinuation))
                        {
                            streamContinuation = readyContinuation;
                            hasItem = false;
                            waitTime = TimeSpan.Zero;
                            jitterMetadata = default;
                            continuationStreamId = readyContinuation!.StreamIds.FirstOrDefault();
                            activity = ReceiveWorkerActivity.RunningContinuation;
                        }
                        else
                        {
                            hasItem = pending.TryDequeue(
                                scheduler.GetTimestamp(),
                                drain: !accepting,
                                out item,
                                out waitTime,
                                out jitterMetadata);
                            if (hasItem)
                            {
                                pendingDepthChanged(-1);
                                processingStreamId = item.Traffic.StreamId;
                                activity = ReceiveWorkerActivity.ProcessingFrame;
                            }
                            else
                            {
                                activity = ReceiveWorkerActivity.Waiting;
                            }
                        }
                        if (!hasItem && wakeSignal.TryConsume())
                            timing.RecordSpuriousWake();
                        if (streamContinuation is null && !hasItem &&
                            !accepting && pending.Count == 0 && streamContinuations.Count == 0)
                        {
                            running = false;
                            completion.TrySetResult();
                            return;
                        }
                    }

                    if (streamContinuation is not null)
                    {
                        try
                        {
                            await streamContinuation.Continuation(lifetime.Token).ConfigureAwait(false);
                            streamContinuation.Completion.TrySetResult();
                        }
                        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                        {
                            streamContinuation.Completion.TrySetCanceled(lifetime.Token);
                        }
                        catch (Exception exception)
                        {
                            streamContinuation.Completion.TrySetException(exception);
                        }
                        finally
                        {
                            lock (sync)
                            {
                                continuationStreamId = null;
                                activity = ReceiveWorkerActivity.Waiting;
                            }
                        }
                        continue;
                    }

                    if (!hasItem)
                    {
                        bool signaled = await scheduler.WaitAsync(wakeSignal, waitTime)
                            .ConfigureAwait(false);
                        timing.RecordWakeWait(signaled);
                        continue;
                    }

                    long processingStarted = scheduler.GetTimestamp();
                    ReceiveProcessingStageTiming processingStages = default;
                    try
                    {
                        if (processIngressWithTiming is not null)
                        {
                            processingStages = await processIngressWithTiming(
                                    channelId,
                                    item.Ingress,
                                    lifetime.Token)
                                .ConfigureAwait(false);
                        }
                        else if (processWithTiming is not null)
                        {
                            processingStages = await processWithTiming(
                                    channelId,
                                    item.Traffic,
                                    lifetime.Token)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            await process!(channelId, item.Traffic, lifetime.Token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                    {
                    }
                    catch
                    {
                        // The application processor reports channel-specific
                        // failures. Keep this worker alive so a fault cannot strand
                        // later lifecycle or terminator frames.
                    }
                    finally
                    {
                        long processingCompleted = scheduler.GetTimestamp();
                        TimeSpan queueDelay = scheduler.GetElapsedTime(
                            item.EnqueuedTimestamp,
                            processingStarted);
                        TimeSpan jitterHold = CalculateJitterHoldDuration(
                            item.EnqueuedTimestamp,
                            processingStarted,
                            jitterMetadata.ReleaseDeadlineTimestamp);
                        TimeSpan orderedDrainHold = CalculateJitterHoldDuration(
                            item.EnqueuedTimestamp,
                            processingStarted,
                            jitterMetadata.OrderedDrainDeadlineTimestamp);
                        var observed = new ReceiveWorkItemTiming(
                            item.Traffic,
                            item.InterArrivalDelay,
                            scheduler.GetElapsedTime(item.IngressTimestamp, item.EnqueuedTimestamp),
                            queueDelay,
                            scheduler.GetElapsedTime(processingStarted, processingCompleted),
                            scheduler.GetElapsedTime(item.IngressTimestamp, processingCompleted),
                            item.TransportInterArrivalDelay,
                            item.TransportToApplicationBoundaryDelay,
                            jitterMetadata.TargetDelay,
                            jitterMetadata.IsAdaptive,
                            jitterMetadata.ReorderedBeforePlayout,
                            jitterMetadata.MissingPacketsAtDeadline,
                            jitterHold,
                            SubtractNonNegative(queueDelay, jitterHold + orderedDrainHold),
                            processingStages.SessionGateDelay,
                            processingStages.SessionProcessingDuration,
                            processingStages.EncryptedSessionProcessing,
                            HasQueueDelayBreakdown: true,
                            HasSessionProcessingBreakdown:
                                processingStages.HasSessionProcessingBreakdown,
                            OrderedDrainHoldDuration: orderedDrainHold);
                        timing.Observe(observed);
                        try
                        {
                            timingObserver?.Invoke(channelId, observed);
                        }
                        catch
                        {
                            // Timing is diagnostic only and must never strand the
                            // ordered decoder worker or later lifecycle traffic.
                        }
                        if (RadioReceiveTrafficClassifier.IsTerminator(item.Traffic))
                            timing.EndStream(item.Traffic.StreamId);
                        lock (sync)
                        {
                            processingStreamId = null;
                            activity = ReceiveWorkerActivity.Waiting;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                terminalFailure = exception;
            }
            finally
            {
                lock (sync)
                {
                    running = false;
                    processingStreamId = null;
                    continuationStreamId = null;
                    activity = ReceiveWorkerActivity.Waiting;
                    foreach (StreamContinuation continuation in streamContinuations)
                    {
                        if (terminalFailure is null)
                            continuation.Completion.TrySetCanceled(lifetime.Token);
                        else
                            continuation.Completion.TrySetException(terminalFailure);
                    }
                    streamContinuations.Clear();
                    if (terminalFailure is null)
                        completion.TrySetResult();
                    else
                        completion.TrySetException(terminalFailure);
                }
            }
        }

        private bool TryTakeReadyStreamContinuation(
            out StreamContinuation? continuation)
        {
            for (int index = 0; index < streamContinuations.Count; index++)
            {
                StreamContinuation candidate = streamContinuations[index];
                if ((processingStreamId is uint processing && candidate.StreamIds.Contains(processing)) ||
                    candidate.StreamIds.Any(pending.ContainsStream))
                {
                    continue;
                }

                foreach (uint streamId in candidate.StreamIds)
                    pending.ForgetStream(streamId);
                streamContinuations.RemoveAt(index);
                continuation = candidate;
                return true;
            }

            continuation = null;
            return false;
        }

        private TimeSpan CalculateJitterHoldDuration(
            long enqueuedTimestamp,
            long processingStarted,
            long releaseDeadlineTimestamp)
        {
            if (releaseDeadlineTimestamp <= enqueuedTimestamp)
                return TimeSpan.Zero;

            long holdCompleted = Math.Min(processingStarted, releaseDeadlineTimestamp);
            return holdCompleted <= enqueuedTimestamp
                ? TimeSpan.Zero
                : scheduler.GetElapsedTime(enqueuedTimestamp, holdCompleted);
        }

        private static TimeSpan SubtractNonNegative(TimeSpan total, TimeSpan part)
            => total > part ? total - part : TimeSpan.Zero;

        private readonly record struct WorkItem(
            RadioMediaIngressFrame Ingress,
            TimeSpan InterArrivalDelay,
            TimeSpan TransportInterArrivalDelay,
            TimeSpan TransportToApplicationBoundaryDelay,
            long EnqueuedTimestamp,
            ReceiveJitterBufferProfile JitterBufferProfile)
        {
            public IRadioMediaFrame Traffic => Ingress.Traffic;
            public long IngressTimestamp => Ingress.BoundaryTimestamp;
        }

        private sealed class StreamContinuation(
            IReadOnlyCollection<uint> streamIds,
            Func<CancellationToken, Task> continuation)
        {
            public HashSet<uint> StreamIds { get; } = streamIds.ToHashSet();
            public Func<CancellationToken, Task> Continuation { get; } = continuation;
            public TaskCompletionSource Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private static int SaturatingAdd(int left, int right)
        => left > int.MaxValue - right ? int.MaxValue : left + right;

    private static long SaturatingAdd(long left, long right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;

    private static void ObserveBackground(Task task)
        => _ = ObserveBackgroundAsync(task);

    private static async Task ObserveBackgroundAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Queue disposal and worker cancellation are expected.
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(
                "Receive work queue background task failed: {0}",
                exception);
        }
    }

    private readonly record struct ReceiveIngressTiming(
        TimeSpan ApplicationInterArrivalDelay,
        TimeSpan TransportInterArrivalDelay,
        TimeSpan TransportToApplicationBoundaryDelay);

    private sealed class TimingAccumulator
    {
        private const int MaximumTrackedStreams = 32;
        private readonly object sync = new();
        private readonly IReceiveWorkQueueScheduler scheduler;
        private readonly Dictionary<uint, StreamTimingAccumulator> streams = [];
        private readonly LinkedList<uint> streamOrder = [];
        private long wakeSignals;
        private long coalescedWakeSignals;
        private long wakeWaits;
        private long wakeTimeouts;
        private long spuriousWakeSignals;
        private long workerStarts;
        private int peakPendingFrames;

        public TimingAccumulator(IReceiveWorkQueueScheduler scheduler)
            => this.scheduler = scheduler;

        public ReceiveIngressTiming ObserveIngress(
            uint streamId,
            long transportIngressTimestamp,
            long applicationBoundaryTimestamp)
        {
            lock (sync)
                return GetOrCreate(streamId).ObserveIngress(
                    transportIngressTimestamp,
                    applicationBoundaryTimestamp);
        }

        public void Observe(ReceiveWorkItemTiming observed)
        {
            lock (sync)
                GetOrCreate(observed.Traffic.StreamId).Observe(observed);
        }

        public void RecordWakeRequest(bool signaled, int pendingFrames)
        {
            lock (sync)
            {
                if (signaled)
                    wakeSignals = SaturatingIncrement(wakeSignals);
                else
                    coalescedWakeSignals = SaturatingIncrement(coalescedWakeSignals);
                peakPendingFrames = Math.Max(peakPendingFrames, pendingFrames);
            }
        }

        public void RecordWakeWait(bool signaled)
        {
            lock (sync)
            {
                wakeWaits = SaturatingIncrement(wakeWaits);
                if (!signaled)
                    wakeTimeouts = SaturatingIncrement(wakeTimeouts);
            }
        }

        public void RecordSpuriousWake()
        {
            lock (sync)
                spuriousWakeSignals = SaturatingIncrement(spuriousWakeSignals);
        }

        public void RecordWorkerStart()
        {
            lock (sync)
                workerStarts = SaturatingIncrement(workerStarts);
        }

        public ReceiveWorkQueueDiagnostics Snapshot(uint? streamId)
        {
            lock (sync)
            {
                if (streamId is uint selectedStreamId)
                    return streams.TryGetValue(selectedStreamId, out StreamTimingAccumulator? selected)
                        ? AddQueueMetrics(selected.Snapshot())
                        : default;

                ReceiveWorkQueueDiagnostics aggregate = default;
                foreach (StreamTimingAccumulator stream in streams.Values)
                    aggregate = Combine(aggregate, stream.Snapshot());
                return AddQueueMetrics(aggregate);
            }
        }

        public void EndStream(uint streamId)
        {
            lock (sync)
            {
                if (!streams.Remove(streamId, out StreamTimingAccumulator? stream))
                    return;
                streamOrder.Remove(stream.OrderNode);
            }
        }

        private StreamTimingAccumulator GetOrCreate(uint streamId)
        {
            if (streams.TryGetValue(streamId, out StreamTimingAccumulator? existing))
                return existing;

            LinkedListNode<uint> orderNode = streamOrder.AddLast(streamId);
            var created = new StreamTimingAccumulator(orderNode, scheduler);
            streams.Add(streamId, created);
            while (streams.Count > MaximumTrackedStreams && streamOrder.First is not null)
            {
                uint oldestStreamId = streamOrder.First.Value;
                streamOrder.RemoveFirst();
                streams.Remove(oldestStreamId);
            }
            return created;
        }

        private static ReceiveWorkQueueDiagnostics Combine(
            ReceiveWorkQueueDiagnostics left,
            ReceiveWorkQueueDiagnostics right)
            => new(
                SaturatingAdd(left.ProcessedFrames, right.ProcessedFrames),
                Max(left.MaximumInterArrivalDelay, right.MaximumInterArrivalDelay),
                Max(left.MaximumIngressToQueueDelay, right.MaximumIngressToQueueDelay),
                Max(left.MaximumQueueDelay, right.MaximumQueueDelay),
                Max(left.MaximumProcessingDuration, right.MaximumProcessingDuration),
                Max(left.MaximumEndToEndDelay, right.MaximumEndToEndDelay),
                Max(left.MaximumTransportInterArrivalDelay, right.MaximumTransportInterArrivalDelay),
                Max(
                    left.MaximumTransportToApplicationBoundaryDelay,
                    right.MaximumTransportToApplicationBoundaryDelay),
                Max(left.MaximumJitterBufferTargetDelay, right.MaximumJitterBufferTargetDelay),
                SaturatingAdd(left.JitterBufferReorderedPackets, right.JitterBufferReorderedPackets),
                SaturatingAdd(left.JitterBufferDeadlineMissedPackets, right.JitterBufferDeadlineMissedPackets),
                MaximumJitterBufferHoldDuration: Max(
                    left.MaximumJitterBufferHoldDuration,
                    right.MaximumJitterBufferHoldDuration),
                MaximumWorkerBacklogDuration: Max(
                    left.MaximumWorkerBacklogDuration,
                    right.MaximumWorkerBacklogDuration),
                MaximumSessionGateDelay: Max(
                    left.MaximumSessionGateDelay,
                    right.MaximumSessionGateDelay),
                MaximumSessionProcessingDuration: Max(
                    left.MaximumSessionProcessingDuration,
                    right.MaximumSessionProcessingDuration),
                MaximumOrderedDrainHoldDuration: Max(
                    left.MaximumOrderedDrainHoldDuration, right.MaximumOrderedDrainHoldDuration));

        private static long SaturatingAdd(long left, long right)
            => left > long.MaxValue - right ? long.MaxValue : left + right;

        private static long SaturatingIncrement(long value)
            => value == long.MaxValue ? long.MaxValue : value + 1;

        private ReceiveWorkQueueDiagnostics AddQueueMetrics(
            ReceiveWorkQueueDiagnostics diagnostics)
            => diagnostics with
            {
                WakeSignals = wakeSignals,
                CoalescedWakeSignals = coalescedWakeSignals,
                WakeWaits = wakeWaits,
                WakeTimeouts = wakeTimeouts,
                PeakPendingFrames = peakPendingFrames,
                SpuriousWakeSignals = spuriousWakeSignals,
                WorkerStarts = workerStarts
            };

        private static TimeSpan Max(TimeSpan left, TimeSpan right)
            => left >= right ? left : right;

        private sealed class StreamTimingAccumulator(
            LinkedListNode<uint> orderNode,
            IReceiveWorkQueueScheduler scheduler)
        {
            private long processedFrames;
            private long lastIngressTimestamp;
            private long lastTransportIngressTimestamp;
            private TimeSpan maximumInterArrivalDelay;
            private TimeSpan maximumTransportInterArrivalDelay;
            private TimeSpan maximumTransportToApplicationBoundaryDelay;
            private TimeSpan maximumIngressToQueueDelay;
            private TimeSpan maximumQueueDelay;
            private TimeSpan maximumProcessingDuration;
            private TimeSpan maximumEndToEndDelay;
            private TimeSpan maximumJitterBufferTargetDelay;
            private TimeSpan maximumJitterBufferHoldDuration;
            private TimeSpan maximumOrderedDrainHoldDuration;
            private TimeSpan maximumWorkerBacklogDuration;
            private TimeSpan maximumSessionGateDelay;
            private TimeSpan maximumSessionProcessingDuration;
            private long jitterBufferReorderedPackets;
            private long jitterBufferDeadlineMissedPackets;

            public LinkedListNode<uint> OrderNode { get; } = orderNode;

            public ReceiveIngressTiming ObserveIngress(
                long transportIngressTimestamp,
                long applicationBoundaryTimestamp)
            {
                TimeSpan applicationDelay = lastIngressTimestamp > 0
                    ? scheduler.GetElapsedTime(lastIngressTimestamp, applicationBoundaryTimestamp)
                    : TimeSpan.Zero;
                TimeSpan transportDelay = transportIngressTimestamp > 0 && lastTransportIngressTimestamp > 0
                    ? scheduler.GetElapsedTime(lastTransportIngressTimestamp, transportIngressTimestamp)
                    : TimeSpan.Zero;
                TimeSpan boundaryDelay = transportIngressTimestamp > 0 &&
                    transportIngressTimestamp <= applicationBoundaryTimestamp
                        ? scheduler.GetElapsedTime(transportIngressTimestamp, applicationBoundaryTimestamp)
                        : TimeSpan.Zero;
                lastIngressTimestamp = applicationBoundaryTimestamp;
                if (transportIngressTimestamp > 0)
                    lastTransportIngressTimestamp = transportIngressTimestamp;
                maximumInterArrivalDelay = Max(maximumInterArrivalDelay, applicationDelay);
                maximumTransportInterArrivalDelay = Max(maximumTransportInterArrivalDelay, transportDelay);
                maximumTransportToApplicationBoundaryDelay = Max(
                    maximumTransportToApplicationBoundaryDelay,
                    boundaryDelay);
                return new ReceiveIngressTiming(applicationDelay, transportDelay, boundaryDelay);
            }

            public void Observe(ReceiveWorkItemTiming observed)
            {
                processedFrames = processedFrames == long.MaxValue ? long.MaxValue : processedFrames + 1;
                maximumIngressToQueueDelay = Max(maximumIngressToQueueDelay, observed.IngressToQueueDelay);
                maximumQueueDelay = Max(maximumQueueDelay, observed.QueueDelay);
                maximumProcessingDuration = Max(maximumProcessingDuration, observed.ProcessingDuration);
                maximumEndToEndDelay = Max(maximumEndToEndDelay, observed.EndToEndDelay);
                maximumJitterBufferTargetDelay = Max(
                    maximumJitterBufferTargetDelay,
                    observed.JitterBufferTargetDelay);
                maximumJitterBufferHoldDuration = Max(
                    maximumJitterBufferHoldDuration,
                    observed.JitterBufferHoldDuration);
                maximumOrderedDrainHoldDuration = Max(
                    maximumOrderedDrainHoldDuration, observed.OrderedDrainHoldDuration);
                maximumWorkerBacklogDuration = Max(
                    maximumWorkerBacklogDuration,
                    observed.WorkerBacklogDuration);
                maximumSessionGateDelay = Max(
                    maximumSessionGateDelay,
                    observed.SessionGateDelay);
                maximumSessionProcessingDuration = Max(
                    maximumSessionProcessingDuration,
                    observed.SessionProcessingDuration);
                if (observed.JitterBufferReorderedPacket && jitterBufferReorderedPackets < long.MaxValue)
                    jitterBufferReorderedPackets++;
                jitterBufferDeadlineMissedPackets = SaturatingAdd(
                    jitterBufferDeadlineMissedPackets,
                    observed.JitterBufferDeadlineMissedPackets);
            }

            public ReceiveWorkQueueDiagnostics Snapshot()
                => new(
                    processedFrames,
                    maximumInterArrivalDelay,
                    maximumIngressToQueueDelay,
                    maximumQueueDelay,
                    maximumProcessingDuration,
                    maximumEndToEndDelay,
                    maximumTransportInterArrivalDelay,
                    maximumTransportToApplicationBoundaryDelay,
                    maximumJitterBufferTargetDelay,
                    jitterBufferReorderedPackets,
                    jitterBufferDeadlineMissedPackets,
                    MaximumJitterBufferHoldDuration: maximumJitterBufferHoldDuration,
                    MaximumWorkerBacklogDuration: maximumWorkerBacklogDuration,
                    MaximumSessionGateDelay: maximumSessionGateDelay,
                    MaximumSessionProcessingDuration: maximumSessionProcessingDuration,
                    MaximumOrderedDrainHoldDuration: maximumOrderedDrainHoldDuration);
        }
    }

    private void ObservePendingDepthChange(int delta)
    {
        if (delta == 0)
            return;

        int current = Interlocked.Add(ref currentPendingFrames, delta);
        if (current < 0)
        {
            Interlocked.Exchange(ref currentPendingFrames, 0);
            current = 0;
        }

        int peak = Volatile.Read(ref peakPendingFrames);
        while (current > peak)
        {
            int observed = Interlocked.CompareExchange(
                ref peakPendingFrames,
                current,
                peak);
            if (observed == peak)
                return;
            peak = observed;
        }
    }
}
