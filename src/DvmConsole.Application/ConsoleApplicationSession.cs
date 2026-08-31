using System.Threading.Channels;

namespace DvmConsole.Application;

public sealed class ConsoleApplicationSession : IConsoleApplicationSession
{
    private readonly object stateSync = new();
    private readonly Func<CancellationToken, ValueTask> quiesce;
    private readonly Func<CancellationToken, ValueTask> flushSettings;
    private readonly Func<ValueTask> dispose;
    private readonly Func<IReadOnlyList<ConsoleCallHistoryRecord>> getHistory;
    private readonly IConsoleSessionRuntimeAdapter? runtimeAdapter;
    private readonly Channel<ChannelMeterSample> meterSamples;
    private readonly Channel<ConsoleLogEvent> logEvents;
    private ConsoleTopologySnapshot topology;
    private ConsoleRuntimeSnapshot snapshot;
    private long nextRevision;
    private int quiesced;
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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(replacement);
        lock (stateSync)
            topology = replacement;
    }

    public ConsoleRuntimeSnapshot PublishSnapshot(ConsoleRuntimeSnapshot replacement)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(replacement);

        ConsoleRuntimeSnapshot previous;
        ConsoleRuntimeSnapshot current;
        lock (stateSync)
        {
            previous = snapshot;
            current = replacement with { Revision = checked(++nextRevision) };
            snapshot = current;
        }

        SnapshotChanged?.Invoke(this, new ConsoleSnapshotChangedEventArgs(previous, current));
        return current;
    }

    public void PublishMeterSample(ChannelMeterSample sample)
    {
        if (Volatile.Read(ref disposed) != 0)
            return;
        meterSamples.Writer.TryWrite(sample);
        MeterSampled?.Invoke(this, sample);
    }

    public void PublishLog(ConsoleLogEvent logEvent)
    {
        if (Volatile.Read(ref disposed) != 0)
            return;
        ArgumentNullException.ThrowIfNull(logEvent);
        logEvents.Writer.TryWrite(logEvent);
        LogPublished?.Invoke(this, logEvent);
    }

    public IAsyncEnumerable<ChannelMeterSample> ReadMeterSamplesAsync(
        CancellationToken cancellationToken = default)
        => meterSamples.Reader.ReadAllAsync(cancellationToken);

    public IAsyncEnumerable<ConsoleLogEvent> ReadLogEventsAsync(
        CancellationToken cancellationToken = default)
        => logEvents.Reader.ReadAllAsync(cancellationToken);

    public async ValueTask QuiesceAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Exchange(ref quiesced, 1) != 0)
            return;

        PublishSnapshot(Snapshot with { IsQuiescing = true });
        try
        {
            await quiesce(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Volatile.Write(ref quiesced, 0);
            PublishSnapshot(Snapshot with { IsQuiescing = false });
            throw;
        }
    }

    public ValueTask FlushSettingsAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return flushSettings(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        if (runtimeAdapter is not null)
        {
            runtimeAdapter.ControlStateInvalidated -= HandleControlStateInvalidated;
            runtimeAdapter.MeterSampled -= HandleMeterSampled;
            runtimeAdapter.LogPublished -= HandleLogPublished;
        }
        meterSamples.Writer.TryComplete();
        logEvents.Writer.TryComplete();
        await dispose().ConfigureAwait(false);
    }

    private void HandleControlStateInvalidated(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref disposed) != 0 || runtimeAdapter is null)
            return;

        ConsoleRuntimeSnapshot captured = runtimeAdapter.CaptureSnapshot();
        ConsoleRuntimeSnapshot current = Snapshot;
        captured = captured with { IsQuiescing = current.IsQuiescing };
        if (!current.HasSameContent(captured))
            PublishSnapshot(captured);
    }

    private void HandleMeterSampled(object? sender, ChannelMeterSample sample)
        => PublishMeterSample(sample);

    private void HandleLogPublished(object? sender, ConsoleLogEvent logEvent)
        => PublishLog(logEvent);

    private static Channel<T> CreateTelemetryChannel<T>(int capacity)
        => Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
}
