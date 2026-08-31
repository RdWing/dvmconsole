using DvmConsole.Core.Runtime;
using DvmConsole.Operations;

namespace DvmConsole.Application;

internal readonly record struct RadioMediaIngressFrame
{
    public RadioMediaIngressFrame(
        IRadioMediaFrame traffic,
        long boundaryTimestamp,
        long transportIngressTimestamp = 0)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        if (boundaryTimestamp <= 0)
            throw new ArgumentOutOfRangeException(nameof(boundaryTimestamp));
        if (transportIngressTimestamp < 0 || transportIngressTimestamp > boundaryTimestamp)
            throw new ArgumentOutOfRangeException(nameof(transportIngressTimestamp));

        Traffic = traffic;
        BoundaryTimestamp = boundaryTimestamp;
        TransportIngressTimestamp = transportIngressTimestamp;
    }

    public IRadioMediaFrame Traffic { get; }
    public long BoundaryTimestamp { get; }
    public long TransportIngressTimestamp { get; }
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
    TimeSpan MaximumSessionProcessingDuration = default);

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
    bool HasSessionProcessingBreakdown = false);

internal readonly record struct ReceiveProcessingStageTiming(
    TimeSpan SessionGateDelay,
    TimeSpan SessionProcessingDuration,
    bool? EncryptedSessionProcessing,
    bool HasSessionProcessingBreakdown = true);

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
    private readonly object sync = new();
    private readonly Dictionary<ChannelId, ChannelWorker> workers = [];
    private readonly Dictionary<ChannelId, TimingAccumulator> timing = [];
    private readonly HashSet<ChannelId> stoppedChannels = [];
    private readonly Func<ChannelId, IRadioMediaFrame, Task>? process;
    private readonly Func<ChannelId, IRadioMediaFrame, Task<ReceiveProcessingStageTiming>>?
        processWithTiming;
    private readonly Action<ChannelId, ReceiveWorkItemTiming>? timingObserver;
    private readonly Func<ChannelId, RadioMediaProtocol, ReceiveJitterBufferProfile> getJitterBufferProfile;
    private readonly IReceiveWorkQueueScheduler scheduler;
    private readonly int maxPendingFramesPerChannel;
    private int currentPendingFrames;
    private int peakPendingFrames;
    private bool disposed;

    public ChannelReceiveWorkQueue(
        Func<ChannelId, IRadioMediaFrame, Task> process,
        int maxPendingFramesPerChannel = 64,
        Action<ChannelId, ReceiveWorkItemTiming>? timingObserver = null,
        Func<ChannelId, RadioMediaProtocol, ReceiveJitterBufferProfile>? getJitterBufferProfile = null,
        IReceiveWorkQueueScheduler? scheduler = null)
        : this(
            process ?? throw new ArgumentNullException(nameof(process)),
            processWithTiming: null,
            maxPendingFramesPerChannel,
            timingObserver,
            getJitterBufferProfile,
            scheduler)
    {
    }

    public static ChannelReceiveWorkQueue CreateWithTiming(
        Func<ChannelId, IRadioMediaFrame, Task<ReceiveProcessingStageTiming>> process,
        int maxPendingFramesPerChannel = 64,
        Action<ChannelId, ReceiveWorkItemTiming>? timingObserver = null,
        Func<ChannelId, RadioMediaProtocol, ReceiveJitterBufferProfile>? getJitterBufferProfile = null,
        IReceiveWorkQueueScheduler? scheduler = null)
        => new(
            process: null,
            processWithTiming: process ?? throw new ArgumentNullException(nameof(process)),
            maxPendingFramesPerChannel,
            timingObserver,
            getJitterBufferProfile,
            scheduler);

    private ChannelReceiveWorkQueue(
        Func<ChannelId, IRadioMediaFrame, Task>? process,
        Func<ChannelId, IRadioMediaFrame, Task<ReceiveProcessingStageTiming>>? processWithTiming,
        int maxPendingFramesPerChannel,
        Action<ChannelId, ReceiveWorkItemTiming>? timingObserver,
        Func<ChannelId, RadioMediaProtocol, ReceiveJitterBufferProfile>? getJitterBufferProfile,
        IReceiveWorkQueueScheduler? scheduler)
    {
        this.process = process;
        this.processWithTiming = processWithTiming;
        if (maxPendingFramesPerChannel < 1)
            throw new ArgumentOutOfRangeException(nameof(maxPendingFramesPerChannel));
        this.maxPendingFramesPerChannel = maxPendingFramesPerChannel;
        this.timingObserver = timingObserver;
        this.getJitterBufferProfile = getJitterBufferProfile ?? ((_, _) => default);
        this.scheduler = scheduler ?? SystemReceiveWorkQueueScheduler.Instance;
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
                    accumulator,
                    maxPendingFramesPerChannel,
                    timingObserver,
                    getJitterBufferProfile,
                    scheduler,
                    ObservePendingDepthChange);
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
        }

        if (worker is not null)
        {
            worker.Complete();
            await worker.Completion.ConfigureAwait(false);
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

    // Runs one episode-level continuation after every buffered packet for any
    // physical stream in the episode has passed through the channel worker.
    // Unrelated streams retain their own jitter deadlines and do not delay it.
    public Task RunAfterStreamsAsync(
        ChannelId channelId,
        IReadOnlyCollection<uint> streamIds,
        Func<Task> continuation)
    {
        ArgumentNullException.ThrowIfNull(streamIds);
        uint[] normalizedStreamIds = streamIds.Distinct().ToArray();
        if (normalizedStreamIds.Length == 0 || normalizedStreamIds.Any(streamId => streamId == 0))
            throw new ArgumentException("At least one non-zero stream ID is required.", nameof(streamIds));
        ArgumentNullException.ThrowIfNull(continuation);

        ChannelWorker? worker;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            workers.TryGetValue(channelId, out worker);
        }

        return worker is null
            ? continuation()
            : worker.RunAfterStreamsAsync(normalizedStreamIds, continuation);
    }

    public async ValueTask DisposeAsync()
    {
        ChannelWorker[] oldWorkers;
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            oldWorkers = workers.Values.ToArray();
            workers.Clear();
        }

        foreach (ChannelWorker worker in oldWorkers)
            worker.Complete();
        await Task.WhenAll(oldWorkers.Select(worker => worker.Completion)).ConfigureAwait(false);
        foreach (ChannelWorker worker in oldWorkers)
            worker.Dispose();
    }

    private sealed class ChannelWorker
    {
        private readonly object sync = new();
        private readonly ReceivePacketJitterBuffer<WorkItem> pending;
        private readonly CoalescingWakeSignal wakeSignal = new();
        private readonly ChannelId channelId;
        private readonly Func<ChannelId, IRadioMediaFrame, Task>? process;
        private readonly Func<ChannelId, IRadioMediaFrame, Task<ReceiveProcessingStageTiming>>?
            processWithTiming;
        private readonly TimingAccumulator timing;
        private readonly int maxPendingFrames;
        private readonly Action<ChannelId, ReceiveWorkItemTiming>? timingObserver;
        private readonly Func<ChannelId, RadioMediaProtocol, ReceiveJitterBufferProfile> getJitterBufferProfile;
        private readonly IReceiveWorkQueueScheduler scheduler;
        private readonly Action<int> pendingDepthChanged;
        private readonly TaskCompletionSource completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<StreamContinuation> streamContinuations = [];
        private uint? processingStreamId;
        private bool accepting = true;
        private bool running;

        public ChannelWorker(
            ChannelId channelId,
            Func<ChannelId, IRadioMediaFrame, Task>? process,
            Func<ChannelId, IRadioMediaFrame, Task<ReceiveProcessingStageTiming>>? processWithTiming,
            TimingAccumulator timing,
            int maxPendingFrames,
            Action<ChannelId, ReceiveWorkItemTiming>? timingObserver,
            Func<ChannelId, RadioMediaProtocol, ReceiveJitterBufferProfile> getJitterBufferProfile,
            IReceiveWorkQueueScheduler scheduler,
            Action<int> pendingDepthChanged)
        {
            this.channelId = channelId;
            this.process = process;
            this.processWithTiming = processWithTiming;
            this.timing = timing;
            this.maxPendingFrames = maxPendingFrames;
            this.timingObserver = timingObserver;
            this.getJitterBufferProfile = getJitterBufferProfile;
            this.scheduler = scheduler;
            this.pendingDepthChanged = pendingDepthChanged;
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
                    traffic,
                    ingressTiming.ApplicationInterArrivalDelay,
                    ingressTiming.TransportInterArrivalDelay,
                    ingressTiming.TransportToApplicationBoundaryDelay,
                    ingress.BoundaryTimestamp,
                    enqueuedTimestamp,
                    getJitterBufferProfile(channelId, traffic.Protocol)), enqueuedTimestamp);
                pendingDepthChanged(pending.Count - previousPendingCount);
                SignalWake();
                if (!running)
                {
                    running = true;
                    ObserveBackground(Task.Run(ProcessLoopAsync));
                }
                return true;
            }
        }

        public void Complete()
        {
            lock (sync)
            {
                accepting = false;
                SignalWake();
                if (!running)
                    completion.TrySetResult();
            }
        }

        public Task RunAfterStreamsAsync(
            IReadOnlyCollection<uint> streamIds,
            Func<Task> continuation)
        {
            var request = new StreamContinuation(streamIds, continuation);
            lock (sync)
            {
                streamContinuations.Add(request);
                SignalWake();
                if (!running)
                {
                    running = true;
                    ObserveBackground(Task.Run(ProcessLoopAsync));
                }
            }
            return request.Completion.Task;
        }

        public void Dispose()
            => wakeSignal.Dispose();

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
            while (true)
            {
                WorkItem item = default;
                StreamContinuation? streamContinuation = null;
                TimeSpan waitTime;
                ReceiveJitterBufferDequeueMetadata jitterMetadata;
                bool hasItem;
                lock (sync)
                {
                    if (TryTakeReadyStreamContinuation(out streamContinuation))
                    {
                        hasItem = false;
                        waitTime = TimeSpan.Zero;
                        jitterMetadata = default;
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
                        }
                    }
                    if (!hasItem)
                    {
                        // Every signal published before this locked state
                        // inspection is already represented by pending work,
                        // lifecycle state, or a selected continuation. Consume
                        // it now so a drained burst cannot become an immediate
                        // stale wake at a later jitter-buffer deadline.
                        if (wakeSignal.TryConsume())
                            timing.RecordSpuriousWake();
                    }
                    if (streamContinuation is null && !hasItem &&
                        pending.Count == 0 && streamContinuations.Count == 0)
                    {
                        running = false;
                        if (!accepting)
                            completion.TrySetResult();
                        return;
                    }
                }

                if (streamContinuation is not null)
                {
                    try
                    {
                        await streamContinuation.Continuation().ConfigureAwait(false);
                        streamContinuation.Completion.TrySetResult();
                    }
                    catch (Exception exception)
                    {
                        streamContinuation.Completion.TrySetException(exception);
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
                    if (processWithTiming is not null)
                    {
                        processingStages = await processWithTiming(channelId, item.Traffic)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await process!(channelId, item.Traffic).ConfigureAwait(false);
                    }
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
                        SubtractNonNegative(queueDelay, jitterHold),
                        processingStages.SessionGateDelay,
                        processingStages.SessionProcessingDuration,
                        processingStages.EncryptedSessionProcessing,
                        HasQueueDelayBreakdown: true,
                        HasSessionProcessingBreakdown:
                            processingStages.HasSessionProcessingBreakdown);
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
                        processingStreamId = null;
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
            IRadioMediaFrame Traffic,
            TimeSpan InterArrivalDelay,
            TimeSpan TransportInterArrivalDelay,
            TimeSpan TransportToApplicationBoundaryDelay,
            long IngressTimestamp,
            long EnqueuedTimestamp,
            ReceiveJitterBufferProfile JitterBufferProfile);

        private sealed class StreamContinuation(
            IReadOnlyCollection<uint> streamIds,
            Func<Task> continuation)
        {
            public HashSet<uint> StreamIds { get; } = streamIds.ToHashSet();
            public Func<Task> Continuation { get; } = continuation;
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
                    right.MaximumSessionProcessingDuration));

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
                SpuriousWakeSignals = spuriousWakeSignals
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
                    MaximumSessionProcessingDuration: maximumSessionProcessingDuration);
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
