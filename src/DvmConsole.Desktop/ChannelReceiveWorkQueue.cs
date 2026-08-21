using DvmConsole.FneClient;
using System.Diagnostics;

namespace DvmConsole.Desktop;

internal readonly record struct ReceiveWorkQueueDiagnostics(
    long ProcessedFrames,
    TimeSpan MaximumInterArrivalDelay,
    TimeSpan MaximumIngressToQueueDelay,
    TimeSpan MaximumQueueDelay,
    TimeSpan MaximumProcessingDuration,
    TimeSpan MaximumEndToEndDelay);

internal readonly record struct ReceiveWorkItemTiming(
    FneTrafficFrame Traffic,
    TimeSpan InterArrivalDelay,
    TimeSpan IngressToQueueDelay,
    TimeSpan QueueDelay,
    TimeSpan ProcessingDuration,
    TimeSpan EndToEndDelay);

// Keeps receive work ordered for one channel without coupling it to any other
// channel. The bounded pending list prevents a slow decoder or output device
// from growing an unbounded continuation chain during a busy period.
internal sealed class ChannelReceiveWorkQueue : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<ChannelViewModel, ChannelWorker> workers = [];
    private readonly Dictionary<ChannelViewModel, TimingAccumulator> timing = [];
    private readonly HashSet<ChannelViewModel> stoppedChannels = [];
    private readonly Func<ChannelViewModel, FneTrafficFrame, Task> process;
    private readonly Action<ChannelViewModel, ReceiveWorkItemTiming>? timingObserver;
    private readonly int maxPendingFramesPerChannel;
    private bool disposed;

    public ChannelReceiveWorkQueue(
        Func<ChannelViewModel, FneTrafficFrame, Task> process,
        int maxPendingFramesPerChannel = 64,
        Action<ChannelViewModel, ReceiveWorkItemTiming>? timingObserver = null)
    {
        this.process = process ?? throw new ArgumentNullException(nameof(process));
        if (maxPendingFramesPerChannel < 1)
            throw new ArgumentOutOfRangeException(nameof(maxPendingFramesPerChannel));
        this.maxPendingFramesPerChannel = maxPendingFramesPerChannel;
        this.timingObserver = timingObserver;
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

    public ReceiveWorkQueueDiagnostics GetDiagnostics(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        lock (sync)
            return timing.TryGetValue(channel, out TimingAccumulator? current)
                ? current.Snapshot()
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
                    timingObserver);
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
        }
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
    }

    private sealed class ChannelWorker
    {
        private readonly object sync = new();
        private readonly LinkedList<WorkItem> pending = [];
        private readonly ChannelViewModel channel;
        private readonly Func<ChannelViewModel, FneTrafficFrame, Task> process;
        private readonly TimingAccumulator timing;
        private readonly int maxPendingFrames;
        private readonly Action<ChannelViewModel, ReceiveWorkItemTiming>? timingObserver;
        private readonly TaskCompletionSource completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool accepting = true;
        private bool running;

        public ChannelWorker(
            ChannelViewModel channel,
            Func<ChannelViewModel, FneTrafficFrame, Task> process,
            TimingAccumulator timing,
            int maxPendingFrames,
            Action<ChannelViewModel, ReceiveWorkItemTiming>? timingObserver)
        {
            this.channel = channel;
            this.process = process;
            this.timing = timing;
            this.maxPendingFrames = maxPendingFrames;
            this.timingObserver = timingObserver;
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
                pending.AddLast(new WorkItem(
                    traffic,
                    timing.ObserveIngress(traffic.StreamId, normalizedIngressTimestamp),
                    normalizedIngressTimestamp,
                    Stopwatch.GetTimestamp()));
                if (!running)
                {
                    running = true;
                    _ = Task.Run(ProcessLoopAsync);
                }
                return true;
            }
        }

        public void Complete()
        {
            lock (sync)
            {
                accepting = false;
                if (!running)
                    completion.TrySetResult();
            }
        }

        private bool MakeRoomFor(FneTrafficFrame incoming)
        {
            LinkedListNode<WorkItem>? candidate = pending.First;
            while (candidate is not null)
            {
                if (!ReceiveTrafficClassifier.IsTerminator(candidate.Value.Traffic) &&
                    HasLaterVoiceForSameStream(candidate))
                {
                    break;
                }
                candidate = candidate.Next;
            }

            if (candidate is null && ReceiveTrafficClassifier.IsTerminator(incoming))
            {
                candidate = pending.First;
                while (candidate is not null &&
                       (ReceiveTrafficClassifier.IsTerminator(candidate.Value.Traffic) ||
                        candidate.Value.Traffic.StreamId == incoming.StreamId))
                {
                    candidate = candidate.Next;
                }
            }

            candidate ??= pending.First;
            while (candidate is not null &&
                   ReceiveTrafficClassifier.IsTerminator(candidate.Value.Traffic))
                candidate = candidate.Next;

            if (candidate is not null)
            {
                pending.Remove(candidate);
                return true;
            }

            if (!ReceiveTrafficClassifier.IsTerminator(incoming))
                return false;

            pending.RemoveFirst();
            return true;
        }

        private static bool HasLaterVoiceForSameStream(LinkedListNode<WorkItem> candidate)
        {
            for (LinkedListNode<WorkItem>? later = candidate.Next; later is not null; later = later.Next)
            {
                if (!ReceiveTrafficClassifier.IsTerminator(later.Value.Traffic) &&
                    later.Value.Traffic.StreamId == candidate.Value.Traffic.StreamId)
                    return true;
            }
            return false;
        }

        private async Task ProcessLoopAsync()
        {
            while (true)
            {
                WorkItem item;
                lock (sync)
                {
                    if (pending.First is null)
                    {
                        running = false;
                        if (!accepting)
                            completion.TrySetResult();
                        return;
                    }

                    item = pending.First.Value;
                    pending.RemoveFirst();
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
                        Stopwatch.GetElapsedTime(item.IngressTimestamp, processingCompleted));
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
                }
            }
        }

        private readonly record struct WorkItem(
            FneTrafficFrame Traffic,
            TimeSpan InterArrivalDelay,
            long IngressTimestamp,
            long EnqueuedTimestamp);
    }

    private sealed class TimingAccumulator
    {
        private readonly object sync = new();
        private long processedFrames;
        private readonly Dictionary<uint, long> lastIngressByStream = [];
        private readonly Queue<uint> ingressStreamOrder = [];
        private TimeSpan maximumInterArrivalDelay;
        private TimeSpan maximumIngressToQueueDelay;
        private TimeSpan maximumQueueDelay;
        private TimeSpan maximumProcessingDuration;
        private TimeSpan maximumEndToEndDelay;

        public TimeSpan ObserveIngress(uint streamId, long ingressTimestamp)
        {
            lock (sync)
            {
                TimeSpan delay = lastIngressByStream.TryGetValue(streamId, out long previous)
                    ? Stopwatch.GetElapsedTime(previous, ingressTimestamp)
                    : TimeSpan.Zero;
                if (!lastIngressByStream.ContainsKey(streamId))
                {
                    ingressStreamOrder.Enqueue(streamId);
                    while (ingressStreamOrder.Count > 32)
                        lastIngressByStream.Remove(ingressStreamOrder.Dequeue());
                }
                lastIngressByStream[streamId] = ingressTimestamp;
                maximumInterArrivalDelay = Max(maximumInterArrivalDelay, delay);
                return delay;
            }
        }

        public void Observe(ReceiveWorkItemTiming observed)
        {
            lock (sync)
            {
                processedFrames++;
                maximumIngressToQueueDelay = Max(
                    maximumIngressToQueueDelay,
                    observed.IngressToQueueDelay);
                maximumQueueDelay = Max(maximumQueueDelay, observed.QueueDelay);
                maximumProcessingDuration = Max(
                    maximumProcessingDuration,
                    observed.ProcessingDuration);
                maximumEndToEndDelay = Max(maximumEndToEndDelay, observed.EndToEndDelay);
            }
        }

        public ReceiveWorkQueueDiagnostics Snapshot()
        {
            lock (sync)
            {
                return new ReceiveWorkQueueDiagnostics(
                    processedFrames,
                    maximumInterArrivalDelay,
                    maximumIngressToQueueDelay,
                    maximumQueueDelay,
                    maximumProcessingDuration,
                    maximumEndToEndDelay);
            }
        }

        private static TimeSpan Max(TimeSpan left, TimeSpan right)
            => left >= right ? left : right;
    }
}
