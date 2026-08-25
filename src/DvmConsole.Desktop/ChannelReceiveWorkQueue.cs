using DvmConsole.FneClient;
using DvmConsole.Operations;
using System.Diagnostics;

namespace DvmConsole.Desktop;

internal readonly record struct ReceiveWorkQueueDiagnostics(
    long ProcessedFrames,
    TimeSpan MaximumInterArrivalDelay,
    TimeSpan MaximumIngressToQueueDelay,
    TimeSpan MaximumQueueDelay,
    TimeSpan MaximumProcessingDuration,
    TimeSpan MaximumEndToEndDelay,
    TimeSpan MaximumTransportInterArrivalDelay = default,
    TimeSpan MaximumTransportToFneBoundaryDelay = default,
    TimeSpan MaximumJitterBufferTargetDelay = default,
    long JitterBufferReorderedPackets = 0,
    long JitterBufferDeadlineMissedPackets = 0,
    long WakeSignals = 0,
    long CoalescedWakeSignals = 0,
    long WakeWaits = 0,
    long WakeTimeouts = 0,
    int PeakPendingFrames = 0,
    long SpuriousWakeSignals = 0);

internal readonly record struct ReceiveWorkItemTiming(
    FneTrafficFrame Traffic,
    TimeSpan InterArrivalDelay,
    TimeSpan IngressToQueueDelay,
    TimeSpan QueueDelay,
    TimeSpan ProcessingDuration,
    TimeSpan EndToEndDelay,
    TimeSpan TransportInterArrivalDelay = default,
    TimeSpan TransportToFneBoundaryDelay = default,
    TimeSpan JitterBufferTargetDelay = default,
    bool AdaptiveJitterBuffer = false,
    bool JitterBufferReorderedPacket = false,
    int JitterBufferDeadlineMissedPackets = 0);

internal interface IReceiveWorkQueueScheduler
{
    long GetTimestamp();
    ValueTask<bool> WaitAsync(CoalescingWakeSignal signal, TimeSpan timeout);
}

internal sealed class SystemReceiveWorkQueueScheduler : IReceiveWorkQueueScheduler
{
    public static SystemReceiveWorkQueueScheduler Instance { get; } = new();

    private SystemReceiveWorkQueueScheduler()
    {
    }

    public long GetTimestamp()
        => Stopwatch.GetTimestamp();

    public ValueTask<bool> WaitAsync(CoalescingWakeSignal signal, TimeSpan timeout)
        => signal.WaitAsync(timeout);
}

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
    private readonly Dictionary<ChannelViewModel, ChannelWorker> workers = [];
    private readonly Dictionary<ChannelViewModel, TimingAccumulator> timing = [];
    private readonly HashSet<ChannelViewModel> stoppedChannels = [];
    private readonly Func<ChannelViewModel, FneTrafficFrame, Task> process;
    private readonly Action<ChannelViewModel, ReceiveWorkItemTiming>? timingObserver;
    private readonly Func<ChannelViewModel, FneTrafficProtocol, ReceiveJitterBufferProfile> getJitterBufferProfile;
    private readonly IReceiveWorkQueueScheduler scheduler;
    private readonly int maxPendingFramesPerChannel;
    private int currentPendingFrames;
    private int peakPendingFrames;
    private bool disposed;

    public ChannelReceiveWorkQueue(
        Func<ChannelViewModel, FneTrafficFrame, Task> process,
        int maxPendingFramesPerChannel = 64,
        Action<ChannelViewModel, ReceiveWorkItemTiming>? timingObserver = null,
        Func<ChannelViewModel, FneTrafficProtocol, ReceiveJitterBufferProfile>? getJitterBufferProfile = null,
        IReceiveWorkQueueScheduler? scheduler = null)
    {
        this.process = process ?? throw new ArgumentNullException(nameof(process));
        if (maxPendingFramesPerChannel < 1)
            throw new ArgumentOutOfRangeException(nameof(maxPendingFramesPerChannel));
        this.maxPendingFramesPerChannel = maxPendingFramesPerChannel;
        this.timingObserver = timingObserver;
        this.getJitterBufferProfile = getJitterBufferProfile ?? ((_, _) => default);
        this.scheduler = scheduler ?? SystemReceiveWorkQueueScheduler.Instance;
    }

    public void Start(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            stoppedChannels.Remove(channel);
            if (!workers.ContainsKey(channel))
                timing[channel] = new TimingAccumulator();
        }
    }

    public ReceiveWorkQueueDiagnostics GetDiagnostics(
        ChannelViewModel channel,
        uint? streamId = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        lock (sync)
            return timing.TryGetValue(channel, out TimingAccumulator? current)
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

    public bool Enqueue(ChannelViewModel channel, FneTrafficFrame traffic)
        => Enqueue(channel, traffic, scheduler.GetTimestamp(), out _);

    public bool Enqueue(
        ChannelViewModel channel,
        FneTrafficFrame traffic,
        out bool droppedFrame)
        => Enqueue(channel, traffic, scheduler.GetTimestamp(), out droppedFrame);

    public bool Enqueue(
        ChannelViewModel channel,
        FneTrafficFrame traffic,
        long ingressTimestamp,
        out bool droppedFrame)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(traffic);

        lock (sync)
        {
            if (disposed || stoppedChannels.Contains(channel))
            {
                droppedFrame = true;
                return false;
            }

            if (!workers.TryGetValue(channel, out ChannelWorker? worker))
            {
                if (!timing.TryGetValue(channel, out TimingAccumulator? accumulator))
                {
                    accumulator = new TimingAccumulator();
                    timing.Add(channel, accumulator);
                }
                worker = new ChannelWorker(
                    channel,
                    process,
                    accumulator,
                    maxPendingFramesPerChannel,
                    timingObserver,
                    getJitterBufferProfile,
                    scheduler,
                    ObservePendingDepthChange);
                workers.Add(channel, worker);
            }

            return worker.Enqueue(traffic, ingressTimestamp, out droppedFrame);
        }
    }

    public async Task StopAsync(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ChannelWorker? worker;
        lock (sync)
        {
            stoppedChannels.Add(channel);
            workers.Remove(channel, out worker);
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
        ChannelViewModel channel,
        uint streamId,
        Func<Task> continuation)
        => RunAfterStreamsAsync(channel, [streamId], continuation);

    // Runs one episode-level continuation after every buffered packet for any
    // physical stream in the episode has passed through the channel worker.
    // Unrelated streams retain their own jitter deadlines and do not delay it.
    public Task RunAfterStreamsAsync(
        ChannelViewModel channel,
        IReadOnlyCollection<uint> streamIds,
        Func<Task> continuation)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(streamIds);
        uint[] normalizedStreamIds = streamIds.Distinct().ToArray();
        if (normalizedStreamIds.Length == 0 || normalizedStreamIds.Any(streamId => streamId == 0))
            throw new ArgumentException("At least one non-zero stream ID is required.", nameof(streamIds));
        ArgumentNullException.ThrowIfNull(continuation);

        ChannelWorker? worker;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            workers.TryGetValue(channel, out worker);
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
        private readonly ChannelViewModel channel;
        private readonly Func<ChannelViewModel, FneTrafficFrame, Task> process;
        private readonly TimingAccumulator timing;
        private readonly int maxPendingFrames;
        private readonly Action<ChannelViewModel, ReceiveWorkItemTiming>? timingObserver;
        private readonly Func<ChannelViewModel, FneTrafficProtocol, ReceiveJitterBufferProfile> getJitterBufferProfile;
        private readonly IReceiveWorkQueueScheduler scheduler;
        private readonly Action<int> pendingDepthChanged;
        private readonly TaskCompletionSource completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<StreamContinuation> streamContinuations = [];
        private uint? processingStreamId;
        private bool accepting = true;
        private bool running;

        public ChannelWorker(
            ChannelViewModel channel,
            Func<ChannelViewModel, FneTrafficFrame, Task> process,
            TimingAccumulator timing,
            int maxPendingFrames,
            Action<ChannelViewModel, ReceiveWorkItemTiming>? timingObserver,
            Func<ChannelViewModel, FneTrafficProtocol, ReceiveJitterBufferProfile> getJitterBufferProfile,
            IReceiveWorkQueueScheduler scheduler,
            Action<int> pendingDepthChanged)
        {
            this.channel = channel;
            this.process = process;
            this.timing = timing;
            this.maxPendingFrames = maxPendingFrames;
            this.timingObserver = timingObserver;
            this.getJitterBufferProfile = getJitterBufferProfile;
            this.scheduler = scheduler;
            this.pendingDepthChanged = pendingDepthChanged;
            pending = new ReceivePacketJitterBuffer<WorkItem>(
                item => item.Traffic.StreamId,
                item => item.Traffic.PacketSequence,
                item => ReceiveTrafficClassifier.GetJitterPacketKind(item.Traffic),
                item => item.JitterBufferProfile);
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
            FneTrafficFrame traffic,
            long ingressTimestamp,
            out bool droppedFrame)
        {
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

                long normalizedIngressTimestamp = ingressTimestamp > 0
                    ? ingressTimestamp
                    : scheduler.GetTimestamp();
                ReceiveIngressTiming ingressTiming = timing.ObserveIngress(
                    traffic.StreamId,
                    traffic.TransportIngressTimestamp,
                    normalizedIngressTimestamp);
                long enqueuedTimestamp = scheduler.GetTimestamp();
                pending.Enqueue(new WorkItem(
                    traffic,
                    ingressTiming.FneInterArrivalDelay,
                    ingressTiming.TransportInterArrivalDelay,
                    ingressTiming.TransportToFneBoundaryDelay,
                    normalizedIngressTimestamp,
                    enqueuedTimestamp,
                    getJitterBufferProfile(channel, traffic.Protocol)), enqueuedTimestamp);
                pendingDepthChanged(pending.Count - previousPendingCount);
                SignalWake();
                if (!running)
                {
                    running = true;
                    TaskObservation.Observe(Task.Run(ProcessLoopAsync));
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
                    TaskObservation.Observe(Task.Run(ProcessLoopAsync));
                }
            }
            return request.Completion.Task;
        }

        public void Dispose()
            => wakeSignal.Dispose();

        private void SignalWake()
            => timing.RecordWakeRequest(wakeSignal.Set(), pending.Count);

        private bool MakeRoomFor(FneTrafficFrame incoming)
        {
            if (pending.TryRemoveOldestSuperseded())
                return true;

            if (ReceiveTrafficClassifier.IsTerminator(incoming) &&
                pending.TryRemoveOldest(item =>
                    !ReceiveTrafficClassifier.IsTerminator(item.Traffic) &&
                    item.Traffic.StreamId != incoming.StreamId))
            {
                return true;
            }

            if (pending.TryRemoveOldest(item =>
                    ReceiveTrafficClassifier.GetJitterPacketKind(item.Traffic) ==
                    ReceiveJitterPacketKind.Voice))
                return true;

            if (!ReceiveTrafficClassifier.IsTerminator(incoming))
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
                try
                {
                    await process(channel, item.Traffic).ConfigureAwait(false);
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
                    var observed = new ReceiveWorkItemTiming(
                        item.Traffic,
                        item.InterArrivalDelay,
                        Stopwatch.GetElapsedTime(item.IngressTimestamp, item.EnqueuedTimestamp),
                        Stopwatch.GetElapsedTime(item.EnqueuedTimestamp, processingStarted),
                        Stopwatch.GetElapsedTime(processingStarted, processingCompleted),
                        Stopwatch.GetElapsedTime(item.IngressTimestamp, processingCompleted),
                        item.TransportInterArrivalDelay,
                        item.TransportToFneBoundaryDelay,
                        jitterMetadata.TargetDelay,
                        jitterMetadata.IsAdaptive,
                        jitterMetadata.ReorderedBeforePlayout,
                        jitterMetadata.MissingPacketsAtDeadline);
                    timing.Observe(observed);
                    try
                    {
                        timingObserver?.Invoke(channel, observed);
                    }
                    catch
                    {
                        // Timing is diagnostic only and must never strand the
                        // ordered decoder worker or later lifecycle traffic.
                    }
                    if (ReceiveTrafficClassifier.IsTerminator(item.Traffic))
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

        private readonly record struct WorkItem(
            FneTrafficFrame Traffic,
            TimeSpan InterArrivalDelay,
            TimeSpan TransportInterArrivalDelay,
            TimeSpan TransportToFneBoundaryDelay,
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

    private readonly record struct ReceiveIngressTiming(
        TimeSpan FneInterArrivalDelay,
        TimeSpan TransportInterArrivalDelay,
        TimeSpan TransportToFneBoundaryDelay);

    private sealed class TimingAccumulator
    {
        private const int MaximumTrackedStreams = 32;
        private readonly object sync = new();
        private readonly Dictionary<uint, StreamTimingAccumulator> streams = [];
        private readonly LinkedList<uint> streamOrder = [];
        private long wakeSignals;
        private long coalescedWakeSignals;
        private long wakeWaits;
        private long wakeTimeouts;
        private long spuriousWakeSignals;
        private int peakPendingFrames;

        public ReceiveIngressTiming ObserveIngress(
            uint streamId,
            long transportIngressTimestamp,
            long fneBoundaryTimestamp)
        {
            lock (sync)
                return GetOrCreate(streamId).ObserveIngress(
                    transportIngressTimestamp,
                    fneBoundaryTimestamp);
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
            var created = new StreamTimingAccumulator(orderNode);
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
                Max(left.MaximumTransportToFneBoundaryDelay, right.MaximumTransportToFneBoundaryDelay),
                Max(left.MaximumJitterBufferTargetDelay, right.MaximumJitterBufferTargetDelay),
                SaturatingAdd(left.JitterBufferReorderedPackets, right.JitterBufferReorderedPackets),
                SaturatingAdd(left.JitterBufferDeadlineMissedPackets, right.JitterBufferDeadlineMissedPackets));

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

        private sealed class StreamTimingAccumulator(LinkedListNode<uint> orderNode)
        {
            private long processedFrames;
            private long lastIngressTimestamp;
            private long lastTransportIngressTimestamp;
            private TimeSpan maximumInterArrivalDelay;
            private TimeSpan maximumTransportInterArrivalDelay;
            private TimeSpan maximumTransportToFneBoundaryDelay;
            private TimeSpan maximumIngressToQueueDelay;
            private TimeSpan maximumQueueDelay;
            private TimeSpan maximumProcessingDuration;
            private TimeSpan maximumEndToEndDelay;
            private TimeSpan maximumJitterBufferTargetDelay;
            private long jitterBufferReorderedPackets;
            private long jitterBufferDeadlineMissedPackets;

            public LinkedListNode<uint> OrderNode { get; } = orderNode;

            public ReceiveIngressTiming ObserveIngress(
                long transportIngressTimestamp,
                long fneBoundaryTimestamp)
            {
                TimeSpan fneDelay = lastIngressTimestamp > 0
                    ? Stopwatch.GetElapsedTime(lastIngressTimestamp, fneBoundaryTimestamp)
                    : TimeSpan.Zero;
                TimeSpan transportDelay = transportIngressTimestamp > 0 && lastTransportIngressTimestamp > 0
                    ? Stopwatch.GetElapsedTime(lastTransportIngressTimestamp, transportIngressTimestamp)
                    : TimeSpan.Zero;
                TimeSpan boundaryDelay = transportIngressTimestamp > 0 &&
                    transportIngressTimestamp <= fneBoundaryTimestamp
                        ? Stopwatch.GetElapsedTime(transportIngressTimestamp, fneBoundaryTimestamp)
                        : TimeSpan.Zero;
                lastIngressTimestamp = fneBoundaryTimestamp;
                if (transportIngressTimestamp > 0)
                    lastTransportIngressTimestamp = transportIngressTimestamp;
                maximumInterArrivalDelay = Max(maximumInterArrivalDelay, fneDelay);
                maximumTransportInterArrivalDelay = Max(maximumTransportInterArrivalDelay, transportDelay);
                maximumTransportToFneBoundaryDelay = Max(maximumTransportToFneBoundaryDelay, boundaryDelay);
                return new ReceiveIngressTiming(fneDelay, transportDelay, boundaryDelay);
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
                    maximumTransportToFneBoundaryDelay,
                    maximumJitterBufferTargetDelay,
                    jitterBufferReorderedPackets,
                    jitterBufferDeadlineMissedPackets);
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
