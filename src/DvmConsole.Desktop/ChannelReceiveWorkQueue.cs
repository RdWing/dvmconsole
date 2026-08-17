using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

// Keeps receive work ordered for one channel without coupling it to any other
// channel. The bounded pending list prevents a slow decoder or output device
// from growing an unbounded continuation chain during a busy period.
internal sealed class ChannelReceiveWorkQueue : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<ChannelViewModel, ChannelWorker> workers = [];
    private readonly HashSet<ChannelViewModel> stoppedChannels = [];
    private readonly Func<ChannelViewModel, FneTrafficFrame, Task> process;
    private readonly int maxPendingFramesPerChannel;
    private bool disposed;

    public ChannelReceiveWorkQueue(
        Func<ChannelViewModel, FneTrafficFrame, Task> process,
        int maxPendingFramesPerChannel = 12)
    {
        this.process = process ?? throw new ArgumentNullException(nameof(process));
        if (maxPendingFramesPerChannel < 1)
            throw new ArgumentOutOfRangeException(nameof(maxPendingFramesPerChannel));
        this.maxPendingFramesPerChannel = maxPendingFramesPerChannel;
    }

    public void Start(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            stoppedChannels.Remove(channel);
        }
    }

    public bool Enqueue(ChannelViewModel channel, FneTrafficFrame traffic)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(traffic);

        lock (sync)
        {
            if (disposed || stoppedChannels.Contains(channel))
                return false;

            if (!workers.TryGetValue(channel, out ChannelWorker? worker))
            {
                worker = new ChannelWorker(channel, process, maxPendingFramesPerChannel);
                workers.Add(channel, worker);
            }

            return worker.Enqueue(traffic);
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
        private readonly LinkedList<FneTrafficFrame> pending = [];
        private readonly ChannelViewModel channel;
        private readonly Func<ChannelViewModel, FneTrafficFrame, Task> process;
        private readonly int maxPendingFrames;
        private readonly TaskCompletionSource completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool accepting = true;
        private bool running;

        public ChannelWorker(
            ChannelViewModel channel,
            Func<ChannelViewModel, FneTrafficFrame, Task> process,
            int maxPendingFrames)
        {
            this.channel = channel;
            this.process = process;
            this.maxPendingFrames = maxPendingFrames;
        }

        public Task Completion => completion.Task;

        public bool Enqueue(FneTrafficFrame traffic)
        {
            lock (sync)
            {
                if (!accepting)
                    return false;

                if (pending.Count >= maxPendingFrames && !MakeRoomFor(traffic))
                    return false;

                pending.AddLast(traffic);
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
            LinkedListNode<FneTrafficFrame>? candidate = pending.First;
            while (candidate is not null && IsTerminator(candidate.Value))
                candidate = candidate.Next;

            if (candidate is not null)
            {
                pending.Remove(candidate);
                return true;
            }

            if (!IsTerminator(incoming))
                return false;

            pending.RemoveFirst();
            return true;
        }

        private async Task ProcessLoopAsync()
        {
            while (true)
            {
                FneTrafficFrame? traffic;
                lock (sync)
                {
                    if (pending.First is null)
                    {
                        running = false;
                        if (!accepting)
                            completion.TrySetResult();
                        return;
                    }

                    traffic = pending.First.Value;
                    pending.RemoveFirst();
                }

                try
                {
                    await process(channel, traffic).ConfigureAwait(false);
                }
                catch
                {
                    // The application processor reports channel-specific
                    // failures. Keep this worker alive so a fault cannot strand
                    // later lifecycle or terminator frames.
                }
            }
        }

        private static bool IsTerminator(FneTrafficFrame traffic)
        {
            if (traffic.FrameType.Equals("TERMINATOR", StringComparison.OrdinalIgnoreCase))
                return true;

            return traffic.Protocol switch
            {
                FneTrafficProtocol.Dmr => traffic.Subtype.Equals(
                    "TERMINATOR_WITH_LC",
                    StringComparison.OrdinalIgnoreCase),
                FneTrafficProtocol.P25 => traffic.Subtype.Equals("TDU", StringComparison.OrdinalIgnoreCase) ||
                                           traffic.Subtype.Equals("TDULC", StringComparison.OrdinalIgnoreCase),
                FneTrafficProtocol.Analog => traffic.Subtype.Equals("TERMINATOR", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }
    }
}
