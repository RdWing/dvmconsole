// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Threading.Channels;

namespace DvmConsole.Application;

public sealed class ConsoleApplicationSession : IConsoleApplicationSession
{
    private readonly object stateSync = new();
    private readonly object quiesceSync = new();
    private readonly AsyncDisposal disposal = new();
    private readonly CancellationTokenSource lifecycleCancellation = new();
    private readonly Func<CancellationToken, ValueTask> quiesce;
    private readonly Func<CancellationToken, ValueTask> flushSettings;
    private readonly Func<ValueTask> dispose;
    private readonly Func<IReadOnlyList<ConsoleCallHistoryRecord>> getHistory;
    private readonly IConsoleSessionRuntimeAdapter? runtimeAdapter;
    private readonly Channel<ChannelMeterSample> meterSamples;
    private readonly Channel<ConsoleLogEvent> logEvents;
    private ConsoleTopologySnapshot topology;
    private ConsoleRuntimeSnapshot snapshot;
    private TaskCompletionSource? quiesceCompletion;
    private bool quiesceCompleted;
    private long nextRevision;
    private int disposed;

    public ConsoleApplicationSession(
        ConsoleTopologySnapshot topology,
        ConsoleRuntimeSnapshot snapshot,
        IConsoleCommands commands,
        Func<CancellationToken, ValueTask>? quiesce = null,
        Func<CancellationToken, ValueTask>? flushSettings = null,
        Func<ValueTask>? dispose = null,
        Func<IReadOnlyList<ConsoleCallHistoryRecord>>? getHistory = null,
        int telemetryCapacity = 256)
    {
        if (telemetryCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(telemetryCapacity));

        Id = ConsoleSessionId.New();
        this.topology = topology ?? throw new ArgumentNullException(nameof(topology));
        this.snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        Commands = commands ?? throw new ArgumentNullException(nameof(commands));
        this.quiesce = quiesce ?? (_ => ValueTask.CompletedTask);
        this.flushSettings = flushSettings ?? (_ => ValueTask.CompletedTask);
        this.dispose = dispose ?? (() => ValueTask.CompletedTask);
        this.getHistory = getHistory ?? (() => []);
        nextRevision = Math.Max(0, snapshot.Revision);
        meterSamples = CreateTelemetryChannel<ChannelMeterSample>(telemetryCapacity);
        logEvents = CreateTelemetryChannel<ConsoleLogEvent>(telemetryCapacity);
    }

    public ConsoleApplicationSession(
        IConsoleSessionRuntimeAdapter runtimeAdapter,
        int telemetryCapacity = 256)
        : this(
            (runtimeAdapter ?? throw new ArgumentNullException(nameof(runtimeAdapter))).CaptureTopology(),
            runtimeAdapter.CaptureSnapshot(),
            runtimeAdapter.Commands,
            runtimeAdapter.QuiesceAsync,
            runtimeAdapter.FlushSettingsAsync,
            runtimeAdapter.DisposeAsync,
            () => runtimeAdapter.History,
            telemetryCapacity)
    {
        this.runtimeAdapter = runtimeAdapter;
        runtimeAdapter.ControlStateInvalidated += HandleControlStateInvalidated;
        runtimeAdapter.MeterSampled += HandleMeterSampled;
        runtimeAdapter.LogPublished += HandleLogPublished;
    }

    public ConsoleSessionId Id { get; }

    public ConsoleTopologySnapshot Topology
    {
        get
        {
            lock (stateSync)
                return topology;
        }
    }

    public ConsoleRuntimeSnapshot Snapshot
    {
        get
        {
            lock (stateSync)
                return snapshot;
        }
    }

    public IConsoleCommands Commands { get; }
    public IReadOnlyList<ConsoleCallHistoryRecord> History => getHistory();

    public event EventHandler<ConsoleSnapshotChangedEventArgs>? SnapshotChanged;
    public event EventHandler<ChannelMeterSample>? MeterSampled;
    public event EventHandler<ConsoleLogEvent>? LogPublished;

    public void PublishTopology(ConsoleTopologySnapshot replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        lock (stateSync)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            topology = replacement;
        }
    }

    public ConsoleRuntimeSnapshot PublishSnapshot(ConsoleRuntimeSnapshot replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (!TryPublishSnapshot(replacement, out ConsoleRuntimeSnapshot previous, out ConsoleRuntimeSnapshot current, out IReadOnlyCollection<ChannelId> changed))
            throw new ObjectDisposedException(nameof(ConsoleApplicationSession));

        if (ReferenceEquals(previous, current))
            return current;
        PublishSafely(
            SnapshotChanged,
            new ConsoleSnapshotChangedEventArgs(previous, current, changed),
            "snapshot observer");
        return current;
    }

    public void PublishMeterSample(ChannelMeterSample sample)
    {
        if (Volatile.Read(ref disposed) != 0)
            return;
        meterSamples.Writer.TryWrite(sample);
        PublishSafely(MeterSampled, sample, "meter observer");
    }

    public void PublishLog(ConsoleLogEvent logEvent)
    {
        if (Volatile.Read(ref disposed) != 0)
            return;
        ArgumentNullException.ThrowIfNull(logEvent);
        logEvents.Writer.TryWrite(logEvent);
        PublishSafely(LogPublished, logEvent, "log observer");
    }

    public IAsyncEnumerable<ChannelMeterSample> ReadMeterSamplesAsync(
        CancellationToken cancellationToken = default)
        => meterSamples.Reader.ReadAllAsync(cancellationToken);

    public IAsyncEnumerable<ConsoleLogEvent> ReadLogEventsAsync(
        CancellationToken cancellationToken = default)
        => logEvents.Reader.ReadAllAsync(cancellationToken);

    public ValueTask QuiesceAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        TaskCompletionSource current;
        bool start = false;
        lock (quiesceSync)
        {
            if (quiesceCompleted)
                return ValueTask.CompletedTask;
            if (quiesceCompletion is null)
            {
                quiesceCompletion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                start = true;
            }
            current = quiesceCompletion;
        }

        if (start)
            _ = ExecuteQuiesceAsync(current, lifecycleCancellation.Token);
        return cancellationToken.CanBeCanceled
            ? new ValueTask(current.Task.WaitAsync(cancellationToken))
            : new ValueTask(current.Task);
    }

    private async Task ExecuteQuiesceAsync(
        TaskCompletionSource completion,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            TryPublishQuiescingState(isQuiescing: true);
            await quiesce(cancellationToken).ConfigureAwait(false);
            lock (quiesceSync)
                quiesceCompleted = true;
        }
        catch (Exception exception)
        {
            failure = exception;
            lock (quiesceSync)
            {
                if (ReferenceEquals(quiesceCompletion, completion))
                    quiesceCompletion = null;
            }
            TryPublishQuiescingState(isQuiescing: false);
        }
        finally
        {
            if (failure is null)
                completion.TrySetResult();
            else
                completion.TrySetException(failure);
        }
    }

    private void TryPublishQuiescingState(bool isQuiescing)
    {
        try
        {
            if (!TryPublishSnapshot(
                    Snapshot with { IsQuiescing = isQuiescing },
                    out ConsoleRuntimeSnapshot previous,
                    out ConsoleRuntimeSnapshot current,
                    out IReadOnlyCollection<ChannelId> changed))
            {
                return;
            }

            PublishSafely(
                SnapshotChanged,
                new ConsoleSnapshotChangedEventArgs(previous, current, changed),
                "snapshot observer");
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(
                "Console application could not publish quiescing state: {0}",
                exception);
        }
    }

    public ValueTask FlushSettingsAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return flushSettings(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        bool beginDisposal;
        lock (stateSync)
        {
            beginDisposal = disposed == 0;
            disposed = 1;
        }
        if (beginDisposal)
            lifecycleCancellation.Cancel();
        return disposal.RunAsync(DisposeCoreAsync);
    }

    private async Task DisposeCoreAsync()
    {
        if (runtimeAdapter is not null)
        {
            runtimeAdapter.ControlStateInvalidated -= HandleControlStateInvalidated;
            runtimeAdapter.MeterSampled -= HandleMeterSampled;
            runtimeAdapter.LogPublished -= HandleLogPublished;
        }
        meterSamples.Writer.TryComplete();
        logEvents.Writer.TryComplete();
        try
        {
            await dispose().ConfigureAwait(false);
        }
        finally
        {
            DisposeLifecycleCancellationWhenSafe();
        }
    }

    private void DisposeLifecycleCancellationWhenSafe()
    {
        Task? quiesceTask;
        lock (quiesceSync)
            quiesceTask = quiesceCompletion?.Task;

        if (quiesceTask is null || quiesceTask.IsCompleted)
        {
            lifecycleCancellation.Dispose();
            return;
        }

        _ = quiesceTask.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                lifecycleCancellation.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void HandleControlStateInvalidated(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref disposed) != 0 || runtimeAdapter is null)
            return;

        ConsoleRuntimeSnapshot current = Snapshot;
        ConsoleSnapshotUpdate update = runtimeAdapter.CaptureUpdate(current);
        ConsoleRuntimeSnapshot captured = update.Snapshot with { IsQuiescing = current.IsQuiescing };
        if (TryPublishSnapshot(captured, out ConsoleRuntimeSnapshot previous,
                out ConsoleRuntimeSnapshot published, out IReadOnlyCollection<ChannelId> changed,
                update.ChangedChannels, current) && !ReferenceEquals(previous, published))
        {
            PublishSafely(SnapshotChanged,
                new ConsoleSnapshotChangedEventArgs(previous, published, changed), "snapshot observer");
        }
    }

    private bool TryPublishSnapshot(
        ConsoleRuntimeSnapshot replacement,
        out ConsoleRuntimeSnapshot previous,
        out ConsoleRuntimeSnapshot current,
        out IReadOnlyCollection<ChannelId> changed,
        IReadOnlyCollection<ChannelId>? candidates = null,
        ConsoleRuntimeSnapshot? candidateBase = null)
    {
        lock (stateSync)
        {
            previous = snapshot;
            changed = [];
            if (disposed != 0)
            {
                current = snapshot;
                return false;
            }
            changed = FindChangedChannels(previous.Channels, replacement.Channels,
                ReferenceEquals(previous, candidateBase) ? candidates : null);
            if (changed.Count == 0 &&
                Equals(previous.RunningConfiguration, replacement.RunningConfiguration) &&
                previous.IsQuiescing == replacement.IsQuiescing &&
                StringComparer.Ordinal.Equals(previous.StatusText, replacement.StatusText))
            {
                current = previous;
                return true;
            }
            current = replacement with { Revision = checked(++nextRevision) };
            snapshot = current;
            return true;
        }
    }

    private static IReadOnlyCollection<ChannelId> FindChangedChannels(
        IReadOnlyDictionary<ChannelId, ChannelControlSnapshot> previous,
        IReadOnlyDictionary<ChannelId, ChannelControlSnapshot> current,
        IReadOnlyCollection<ChannelId>? candidates)
    {
        if (ReferenceEquals(previous, current))
            return [];
        List<ChannelId>? changed = null;
        if (candidates is not null)
        {
            foreach (ChannelId id in candidates)
            {
                if (!previous.TryGetValue(id, out ChannelControlSnapshot? before) ||
                    !current.TryGetValue(id, out ChannelControlSnapshot? after) || !before.HasSameContent(after))
                    (changed ??= []).Add(id);
            }
        }
        else
        {
            bool added = false;
            foreach ((ChannelId id, ChannelControlSnapshot after) in current)
            {
                if (!previous.TryGetValue(id, out ChannelControlSnapshot? before))
                {
                    added = true;
                    (changed ??= []).Add(id);
                }
                else if (!before.HasSameContent(after))
                    (changed ??= []).Add(id);
            }
            if (added || previous.Count != current.Count)
            {
                foreach (ChannelId id in previous.Keys)
                    if (!current.ContainsKey(id))
                        (changed ??= []).Add(id);
            }
        }
        return changed is null ? [] : changed.ToArray();
    }

    private void HandleMeterSampled(object? sender, ChannelMeterSample sample)
        => PublishMeterSample(sample);

    private void HandleLogPublished(object? sender, ConsoleLogEvent logEvent)
        => PublishLog(logEvent);

    private void PublishSafely<TEventArgs>(
        EventHandler<TEventArgs>? handlers,
        TEventArgs args,
        string observerName)
    {
        if (handlers is null)
            return;

        foreach (EventHandler<TEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceError(
                    "Console application {0} failed: {1}",
                    observerName,
                    exception);
            }
        }
    }

    private static Channel<T> CreateTelemetryChannel<T>(int capacity)
        => Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
}
