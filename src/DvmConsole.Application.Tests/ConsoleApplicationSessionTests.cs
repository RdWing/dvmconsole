using DvmConsole.Application;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ConsoleApplicationSessionTests
{
    [Fact]
    public void ControlSnapshotsReceiveMonotonicallyIncreasingRevisions()
    {
        var session = CreateSession(initialRevision: 19);
        var observed = new List<long>();
        session.SnapshotChanged += (_, args) => observed.Add(args.Current.Revision);

        ConsoleRuntimeSnapshot first = session.PublishSnapshot(session.Snapshot with { StatusText = "one" });
        ConsoleRuntimeSnapshot second = session.PublishSnapshot(session.Snapshot with { StatusText = "two" });

        Assert.Equal(20, first.Revision);
        Assert.Equal(21, second.Revision);
        Assert.Equal([20, 21], observed);
    }

    [Fact]
    public void MeterSamplesDoNotReplaceTheControlSnapshot()
    {
        var session = CreateSession(initialRevision: 8);
        ConsoleRuntimeSnapshot before = session.Snapshot;
        int controlEvents = 0;
        int meterEvents = 0;
        session.SnapshotChanged += (_, _) => controlEvents++;
        session.MeterSampled += (_, _) => meterEvents++;

        session.PublishMeterSample(new ChannelMeterSample(
            default,
            17,
            34,
            DateTimeOffset.UtcNow));

        Assert.Same(before, session.Snapshot);
        Assert.Equal(0, controlEvents);
        Assert.Equal(1, meterEvents);
    }

    [Fact]
    public async Task QuiesceIsIdempotentAndPublishesStateBeforeCallingTheRuntime()
    {
        int quiesceCalls = 0;
        bool observedQuiescing = false;
        ConsoleApplicationSession? session = null;
        session = CreateSession(
            quiesce: _ =>
            {
                quiesceCalls++;
                observedQuiescing = session!.Snapshot.IsQuiescing;
                return ValueTask.CompletedTask;
            });

        await session.QuiesceAsync(CancellationToken.None);
        await session.QuiesceAsync(CancellationToken.None);

        Assert.True(observedQuiescing);
        Assert.True(session.Snapshot.IsQuiescing);
        Assert.Equal(1, quiesceCalls);
    }

    [Fact]
    public async Task FlushAndDisposeUseSeparateHostTransactions()
    {
        int flushCalls = 0;
        int disposeCalls = 0;
        var session = CreateSession(
            flush: _ =>
            {
                flushCalls++;
                return ValueTask.CompletedTask;
            },
            dispose: () =>
            {
                disposeCalls++;
                return ValueTask.CompletedTask;
            });

        await session.FlushSettingsAsync(CancellationToken.None);
        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.Equal(1, flushCalls);
        Assert.Equal(1, disposeCalls);
    }

    [Fact]
    public async Task RuntimeAdapterFeedsApplicationOwnedStateAndLifecycle()
    {
        var adapter = new TestRuntimeAdapter();
        var session = new ConsoleApplicationSession(adapter);
        var revisions = new List<long>();
        session.SnapshotChanged += (_, args) => revisions.Add(args.Current.Revision);

        adapter.StatusText = "updated";
        adapter.InvalidateControlState();

        Assert.Equal("updated", session.Snapshot.StatusText);
        Assert.Equal([1], revisions);
        Assert.Equal(1, adapter.TopologyCaptureCount);

        await session.FlushSettingsAsync(CancellationToken.None);
        await session.QuiesceAsync(CancellationToken.None);
        Assert.True(session.Snapshot.IsQuiescing);
        Assert.Equal(1, adapter.FlushCount);
        Assert.Equal(1, adapter.QuiesceCount);

        await session.DisposeAsync();
        adapter.InvalidateControlState();
        Assert.Equal(1, adapter.DisposeCount);
        Assert.Equal([1, 2], revisions);
    }

    [Fact]
    public async Task EquivalentAdapterInvalidationDoesNotPublishOrRecaptureTopology()
    {
        var adapter = new TestRuntimeAdapter();
        await using var session = new ConsoleApplicationSession(adapter);
        int snapshotEvents = 0;
        session.SnapshotChanged += (_, _) => snapshotEvents++;

        adapter.InvalidateControlState();

        Assert.Equal(0, snapshotEvents);
        Assert.Equal(0, session.Snapshot.Revision);
        Assert.Equal(1, adapter.TopologyCaptureCount);
        Assert.Equal(2, adapter.SnapshotCaptureCount);
    }

    [Fact]
    public async Task AdapterInvalidationCannotClearApplicationOwnedQuiescingState()
    {
        var adapter = new TestRuntimeAdapter { InvalidateDuringQuiesce = true };
        var session = new ConsoleApplicationSession(adapter);

        await session.QuiesceAsync(CancellationToken.None);

        Assert.True(session.Snapshot.IsQuiescing);
        await session.DisposeAsync();
    }

    private static ConsoleApplicationSession CreateSession(
        long initialRevision = 0,
        Func<CancellationToken, ValueTask>? quiesce = null,
        Func<CancellationToken, ValueTask>? flush = null,
        Func<ValueTask>? dispose = null)
        => new(
            ConsoleTopologySnapshot.Empty,
            ConsoleRuntimeSnapshot.Empty with { Revision = initialRevision },
            new NoOpConsoleCommands(),
            quiesce,
            flush,
            dispose);

    private sealed class TestRuntimeAdapter : IConsoleSessionRuntimeAdapter
    {
        public int TopologyCaptureCount { get; private set; }
        public int SnapshotCaptureCount { get; private set; }
        public int FlushCount { get; private set; }
        public int QuiesceCount { get; private set; }
        public int DisposeCount { get; private set; }
        public bool InvalidateDuringQuiesce { get; init; }
        public string StatusText { get; set; } = "initial";
        public IReadOnlyList<ConsoleCallHistoryRecord> History => [];
        public IConsoleCommands Commands { get; } = new NoOpConsoleCommands();

        public event EventHandler? ControlStateInvalidated;
        public event EventHandler<ChannelMeterSample>? MeterSampled;
        public event EventHandler<ConsoleLogEvent>? LogPublished;

        public ConsoleTopologySnapshot CaptureTopology()
        {
            TopologyCaptureCount++;
            return ConsoleTopologySnapshot.Empty;
        }

        public ConsoleRuntimeSnapshot CaptureSnapshot()
        {
            SnapshotCaptureCount++;
            return ConsoleRuntimeSnapshot.Empty with { StatusText = StatusText };
        }

        public ValueTask QuiesceAsync(CancellationToken cancellationToken)
        {
            QuiesceCount++;
            if (InvalidateDuringQuiesce)
                InvalidateControlState();
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushSettingsAsync(CancellationToken cancellationToken)
        {
            FlushCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        public void InvalidateControlState()
            => ControlStateInvalidated?.Invoke(this, EventArgs.Empty);

        public void PublishMeter(ChannelMeterSample sample)
            => MeterSampled?.Invoke(this, sample);

        public void PublishLog(ConsoleLogEvent logEvent)
            => LogPublished?.Invoke(this, logEvent);
    }
}
