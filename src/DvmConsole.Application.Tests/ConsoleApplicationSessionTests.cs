// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ConsoleApplicationSessionTests
{
    [Fact]
    public async Task IncrementalCaptureFallsBackWhenItsBaseWasReplacedWhileCapturing()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new TestRuntimeAdapter
        {
            CaptureEntered = entered,
            ReleaseCapture = release,
            ChangedChannels = []
        };
        await using var session = new ConsoleApplicationSession(adapter);
        Task invalidation = Task.Run(adapter.InvalidateControlState);
        await entered.Task;
        ChannelId id = default;
        var channel = new ChannelControlSnapshot(id, default, "Idle", "", false, false, false,
            false, false, false, false, false, null, false, null, 1, 0, null,
            TargetAuthorityState.Pending, null, false, false, true, [], null, null);
        session.PublishSnapshot(session.Snapshot with { Channels = new Dictionary<ChannelId, ChannelControlSnapshot> { [id] = channel } });
        IReadOnlyCollection<ChannelId>? changed = null;
        session.SnapshotChanged += (_, args) => changed = args.ChangedChannels;
        release.SetResult();
        await invalidation;
        Assert.Empty(session.Snapshot.Channels);
        Assert.Equal([id], changed);
    }

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
    public async Task ConcurrentQuiesceCallersShareTheSameOperation()
    {
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var session = CreateSession(quiesce: async _ =>
        {
            Interlocked.Increment(ref calls);
            await release.Task;
        });

        Task first = session.QuiesceAsync(CancellationToken.None).AsTask();
        Task second = session.QuiesceAsync(CancellationToken.None).AsTask();

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, Volatile.Read(ref calls));
        release.SetResult();
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task CancellingOneQuiesceWaiterDoesNotCancelTheSharedOperation()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var session = CreateSession(quiesce: async cancellationToken =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        });
        using var firstWaiterCancellation = new CancellationTokenSource();

        Task first = session.QuiesceAsync(firstWaiterCancellation.Token).AsTask();
        await entered.Task;
        Task second = session.QuiesceAsync(CancellationToken.None).AsTask();
        firstWaiterCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, Volatile.Read(ref calls));

        release.SetResult();
        await second;
        await session.DisposeAsync();
    }

    [Fact]
    public async Task DisposalCancelsTheSessionOwnedQuiesceOperation()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var session = CreateSession(quiesce: async cancellationToken =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });

        Task quiesce = session.QuiesceAsync(CancellationToken.None).AsTask();
        await entered.Task;
        await session.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => quiesce);
    }

    [Fact]
    public async Task FailedQuiesceCanBeRetried()
    {
        int calls = 0;
        var session = CreateSession(quiesce: _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                return ValueTask.FromException(new IOException("first attempt"));
            return ValueTask.CompletedTask;
        });

        await Assert.ThrowsAsync<IOException>(() =>
            session.QuiesceAsync(CancellationToken.None).AsTask());
        await session.QuiesceAsync(CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.True(session.Snapshot.IsQuiescing);
    }

    [Fact]
    public async Task FailedQuiesceSettlesEveryCallerWhenDisposalWinsTheRace()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var session = CreateSession(quiesce: async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            throw new IOException("quiesce failed");
        });

        Task first = session.QuiesceAsync(CancellationToken.None).AsTask();
        Task second = session.QuiesceAsync(CancellationToken.None).AsTask();
        await entered.Task;
        await session.DisposeAsync();
        release.SetResult();

        await Assert.ThrowsAsync<IOException>(() => first.WaitAsync(TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<IOException>(() => second.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ThrowingObserverCannotBlockStateOrLaterObservers()
    {
        var session = CreateSession();
        int laterCalls = 0;
        session.SnapshotChanged += (_, _) => throw new InvalidOperationException("observer");
        session.SnapshotChanged += (_, _) => laterCalls++;

        ConsoleRuntimeSnapshot published = session.PublishSnapshot(
            session.Snapshot with { StatusText = "safe" });

        Assert.Equal("safe", published.StatusText);
        Assert.Equal(1, laterCalls);
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
    public async Task ConcurrentDisposeCallersObserveTheSameCompletion()
    {
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var session = CreateSession(dispose: async () =>
        {
            Interlocked.Increment(ref calls);
            await release.Task;
        });

        Task first = session.DisposeAsync().AsTask();
        Task second = session.DisposeAsync().AsTask();

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, Volatile.Read(ref calls));
        release.SetResult();
        await Task.WhenAll(first, second);
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

    [Fact]
    public async Task InvalidationAlreadyCapturingStateCannotPublishAfterDisposal()
    {
        var captureEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCapture = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new TestRuntimeAdapter
        {
            CaptureEntered = captureEntered,
            ReleaseCapture = releaseCapture
        };
        var session = new ConsoleApplicationSession(adapter);

        Task invalidation = Task.Run(adapter.InvalidateControlState);
        await captureEntered.Task;
        await session.DisposeAsync();
        releaseCapture.SetResult();

        await invalidation;
        Assert.Equal(0, session.Snapshot.Revision);
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
        public TaskCompletionSource? CaptureEntered { get; init; }
        public TaskCompletionSource? ReleaseCapture { get; init; }
        public IReadOnlyCollection<ChannelId>? ChangedChannels { get; init; }
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
            if (SnapshotCaptureCount > 1 && CaptureEntered is not null && ReleaseCapture is not null)
            {
                CaptureEntered.TrySetResult();
                ReleaseCapture.Task.GetAwaiter().GetResult();
            }
            return ConsoleRuntimeSnapshot.Empty with { StatusText = StatusText };
        }

        public ConsoleSnapshotUpdate CaptureUpdate(ConsoleRuntimeSnapshot previous)
            => new(CaptureSnapshot(), ChangedChannels);

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
