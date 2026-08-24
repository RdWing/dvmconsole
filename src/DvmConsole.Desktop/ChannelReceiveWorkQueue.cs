using DvmConsole.FneClient;
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
    long JitterBufferDeadlineMissedPackets = 0);

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
    private readonly int maxPendingFramesPerChannel;
    private bool disposed;

    public ChannelReceiveWorkQueue(
        Func<ChannelViewModel, FneTrafficFrame, Task> process,
        int maxPendingFramesPerChannel = 64,
        Action<ChannelViewModel, ReceiveWorkItemTiming>? timingObserver = null,
        Func<ChannelViewModel, FneTrafficProtocol, ReceiveJitterBufferProfile>? getJitterBufferProfile = null)
    {
        this.process = process ?? throw new ArgumentNullException(nameof(process));
        if (maxPendingFramesPerChannel < 1)
            throw new ArgumentOutOfRangeException(nameof(maxPendingFramesPerChannel));
        this.maxPendingFramesPerChannel = maxPendingFramesPerChannel;
        this.timingObserver = timingObserver;
        this.getJitterBufferProfile = getJitterBufferProfile ?? ((_, _) => default);
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

    public bool Enqueue(ChannelViewModel channel, FneTrafficFrame traffic)
        => Enqueue(channel, traffic, Stopwatch.GetTimestamp(), out _);

    public bool Enqueue(
        ChannelViewModel channel,
        FneTrafficFrame traffic,
        out bool droppedFrame)
        => Enqueue(channel, traffic, Stopwatch.GetTimestamp(), out droppedFrame);

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
                    getJitterBufferProfile);
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
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (streamId == 0)
            throw new ArgumentOutOfRangeException(nameof(streamId));
        ArgumentNullException.ThrowIfNull(continuation);

        ChannelWorker? worker;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            workers.TryGetValue(channel, out worker);
        }

        return worker is null
            ? continuation()
            : worker.RunAfterStreamAsync(streamId, continuation);
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
        private readonly SemaphoreSlim wakeSignal = new(0);
        private readonly ChannelViewModel channel;
        private readonly Func<ChannelViewModel, FneTrafficFrame, Task> process;
        private readonly TimingAccumulator timing;
        private readonly int maxPendingFrames;
        private readonly Action<ChannelViewModel, ReceiveWorkItemTiming>? timingObserver;
        private readonly Func<ChannelViewModel, FneTrafficProtocol, ReceiveJitterBufferProfile> getJitterBufferProfile;
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
            Func<ChannelViewModel, FneTrafficProtocol, ReceiveJitterBufferProfile> getJitterBufferProfile)
        {
            this.channel = channel;
            this.process = process;
            this.timing = timing;
            this.maxPendingFrames = maxPendingFrames;
            this.timingObserver = timingObserver;
            this.getJitterBufferProfile = getJitterBufferProfile;
            pending = new ReceivePacketJitterBuffer<WorkItem>(
                item => item.Traffic.StreamId,
                item => item.Traffic.PacketSequence,
                item => ReceiveTrafficClassifier.IsTerminator(item.Traffic),
                item => item.JitterBufferProfile);
        }

        public Task Completion => completion.Task;

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

                droppedFrame = pending.Count >= maxPendingFrames;
                if (droppedFrame && !MakeRoomFor(traffic))
                    return false;

                long normalizedIngressTimestamp = ingressTimestamp > 0
                    ? ingressTimestamp
                    : Stopwatch.GetTimestamp();
                ReceiveIngressTiming ingressTiming = timing.ObserveIngress(
                    traffic.StreamId,
                    traffic.TransportIngressTimestamp,
                    normalizedIngressTimestamp);
                long enqueuedTimestamp = Stopwatch.GetTimestamp();
                pending.Enqueue(new WorkItem(
                    traffic,
                    ingressTiming.FneInterArrivalDelay,
                    ingressTiming.TransportInterArrivalDelay,
                    ingressTiming.TransportToFneBoundaryDelay,
                    normalizedIngressTimestamp,
                    enqueuedTimestamp,
                    getJitterBufferProfile(channel, traffic.Protocol)), enqueuedTimestamp);
                wakeSignal.Release();
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
                wakeSignal.Release();
                if (!running)
                    completion.TrySetResult();
            }
        }

        public Task RunAfterStreamAsync(uint streamId, Func<Task> continuation)
        {
            var request = new StreamContinuation(streamId, continuation);
            lock (sync)
            {
                streamContinuations.Add(request);
                wakeSignal.Release();
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
                    !ReceiveTrafficClassifier.IsTerminator(item.Traffic)))
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
                            Stopwatch.GetTimestamp(),
                            drain: !accepting,
                            out item,
                            out waitTime,
                            out jitterMetadata);
                        if (hasItem)
                            processingStreamId = item.Traffic.StreamId;
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
                    if (waitTime == Timeout.InfiniteTimeSpan)
                        await wakeSignal.WaitAsync().ConfigureAwait(false);
                    else
                        await wakeSignal.WaitAsync(waitTime).ConfigureAwait(false);
                    continue;
                }

                long processingStarted = Stopwatch.GetTimestamp();
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
                    long processingCompleted = Stopwatch.GetTimestamp();
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
                if (processingStreamId == candidate.StreamId ||
                    pending.ContainsStream(candidate.StreamId))
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
            uint streamId,
            Func<Task> continuation)
        {
            public uint StreamId { get; } = streamId;
            public Func<Task> Continuation { get; } = continuation;
            public TaskCompletionSource Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

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

        public ReceiveWorkQueueDiagnostics Snapshot(uint? streamId)
        {
            lock (sync)
            {
                if (streamId is uint selectedStreamId)
                    return streams.TryGetValue(selectedStreamId, out StreamTimingAccumulator? selected)
                        ? selected.Snapshot()
                        : default;

                ReceiveWorkQueueDiagnostics aggregate = default;
                foreach (StreamTimingAccumulator stream in streams.Values)
                    aggregate = Combine(aggregate, stream.Snapshot());
                return aggregate;
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
}
