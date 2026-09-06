// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Runtime;
using DvmConsole.Desktop;
using DvmConsole.Operations;
using DvmConsole.FneClient;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ReceiveSessionControllerTests
{
    [Fact]
    public async Task ReconfigurationCanDrainAFailingWorkerAndItsQueuedTerminatorWhileHoldingItsGate()
    {
        using var reconfigurationGate = new SemaphoreSlim(1, 1);
        ChannelId id = CreateChannelId();
        var port = new FakeReceiveSessionPort
        {
            ExclusiveGate = reconfigurationGate,
            ChannelsValue = [new(id, true, true, false)],
            ActiveChannels = [id]
        };
        var controller = new ReceiveSessionController(port);
        var processing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var work = new ChannelReceiveWorkQueue(async (_, frame) =>
        {
            if (frame.PacketSequence == ushort.MaxValue)
            {
                terminated.TrySetResult();
                return;
            }
            processing.TrySetResult();
            await fail.Task;
            await controller.RetireFailedSessionAsync(id);
        });
        FneTrafficFrame Frame(bool terminator) => new(FneTrafficProtocol.Dmr,
            1, 2, 100, 1, "GROUP", terminator ? "TERMINATOR" : "VOICE",
            terminator ? "TERMINATOR_WITH_LC" : "VOICE",
            terminator ? ushort.MaxValue : (ushort)1, 99, []);
        work.Enqueue(id, Frame(false));
        await processing.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await reconfigurationGate.WaitAsync();
        try
        {
            work.Enqueue(id, Frame(true));
            Task stopping = work.StopAsync(id);
            fail.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(terminated.Task.IsCompletedSuccessfully, "Shutdown must drain the queued terminator.");
            Assert.True(controller.IsRetryPending(id));
            Assert.Equal(0, port.ExclusiveCount);
        }
        finally
        {
            fail.TrySetResult();
            reconfigurationGate.Release();
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task ProcessingFailureRetainsIntentAndRestartsAfterCooldown(bool listening, bool recording)
    {
        ChannelId id = CreateChannelId();
        var desired = new ReceiveSessionChannelState(id, listening, recording, false);
        var port = new FakeReceiveSessionPort
        {
            ChannelsValue = [desired],
            ActiveChannels = [id],
            LivePlaybackChannels = listening ? [id] : []
        };
        var controller = new ReceiveSessionController(port);

        await controller.RetireFailedSessionAsync(id);
        Assert.Equal(desired, Assert.Single(port.Channels));
        Assert.False(port.IsActive(id));
        Assert.True(controller.IsRetryPending(id));
        await controller.ReconcileAsync();
        Assert.Equal(0, port.StartCount + port.EnsureDecodeCount);

        port.UtcNowValue = port.UtcNowValue.AddSeconds(5);
        await controller.ReconcileAsync();
        Assert.Equal(listening ? 1 : 0, port.StartCount);
        Assert.Equal(listening ? 0 : 1, port.EnsureDecodeCount);
        Assert.Equal(1, port.StartWorkCount);
        Assert.False(controller.IsRetryPending(id));
        Assert.Equal(desired, Assert.Single(port.Channels));
    }

    [Fact]
    public async Task OperatorDeselectionDuringCooldownPreventsRestart()
    {
        ChannelId id = CreateChannelId();
        var port = new FakeReceiveSessionPort { ChannelsValue = [new(id, true, true, false)] };
        var controller = new ReceiveSessionController(port);
        await controller.RetireFailedSessionAsync(id);
        port.ChannelsValue = [new(id, false, false, false)];
        port.UtcNowValue = port.UtcNowValue.AddSeconds(5);
        await controller.ReconcileAsync();
        Assert.Equal(0, port.StartCount + port.EnsureDecodeCount);
    }

    [Fact]
    public async Task FailedRestartUsesInjectedClockAndRetriesOnlyAfterDelay()
    {
        var port = new FakeReceiveSessionPort
        {
            ChannelsValue = [new(CreateChannelId(), true, false, false)],
            StartFailure = new IOException("route unavailable")
        };
        var controller = new ReceiveSessionController(port);

        await controller.ReconcileAsync();
        await controller.ReconcileAsync();

        Assert.Equal(1, port.StartCount);
        port.UtcNowValue = port.UtcNowValue.AddSeconds(5);
        port.StartFailure = null;
        await controller.ReconcileAsync();

        Assert.Equal(2, port.StartCount);
        Assert.Equal(1, port.StartWorkCount);
        Assert.Equal(1, port.ResetTimingCount);
        Assert.Equal("Restored 1 receive decode session(s).", port.LastStatus);
    }

    [Fact]
    public async Task RecordingOnlyChannelStartsDecodeWithoutOpeningPlayback()
    {
        var port = new FakeReceiveSessionPort
        {
            ChannelsValue = [new(CreateChannelId(), false, true, false)]
        };
        var controller = new ReceiveSessionController(port);

        await controller.ReconcileAsync();

        Assert.Equal(0, port.StartCount);
        Assert.Equal(1, port.EnsureDecodeCount);
        Assert.Equal(1, port.StartWorkCount);
    }

    [Fact]
    public async Task DisposingSessionDoesNotEnterTheRouteGate()
    {
        var port = new FakeReceiveSessionPort
        {
            IsDisposingValue = true,
            ChannelsValue = [new(CreateChannelId(), true, false, false)]
        };
        var controller = new ReceiveSessionController(port);

        await controller.ReconcileAsync();

        Assert.Equal(0, port.ExclusiveCount);
        Assert.Equal(0, port.StartCount);
    }

    [Fact]
    public async Task ReconciliationReadsCurrentFlagsOnEveryPass()
    {
        ChannelId id = CreateChannelId();
        var states = new[] { new ReceiveSessionChannelState(id, false, false, false) };
        var port = new FakeReceiveSessionPort { ChannelsValue = states };
        var controller = new ReceiveSessionController(port);

        await controller.ReconcileAsync();
        Assert.Equal(0, port.StartCount);
        Assert.Equal(0, port.EnsureDecodeCount);

        states[0] = states[0] with { RecordingEnabled = true };
        await controller.ReconcileAsync();
        Assert.Equal(1, port.EnsureDecodeCount);

        states[0] = states[0] with { AudioEnabled = true, RecordingEnabled = false };
        await controller.ReconcileAsync();
        Assert.Equal(1, port.StartCount);
    }

    [Fact]
    public async Task PendingRestartsAreSnapshottedBeforeTheFirstAwait()
    {
        ChannelId first = CreateChannelId();
        ChannelId second = new(new ChannelSessionId("Test", ChannelProtocol.Dmr, 101, 1, "Channel B"));
        var states = new[]
        {
            new ReceiveSessionChannelState(first, true, false, false),
            new ReceiveSessionChannelState(second, true, false, false)
        };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var port = new FakeReceiveSessionPort
        {
            ChannelsValue = states,
            StartOperation = async id =>
            {
                if (id == first)
                {
                    entered.SetResult();
                    await release.Task;
                }
            }
        };
        var controller = new ReceiveSessionController(port);
        Task reconcile = controller.ReconcileAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            states[1] = states[1] with { AudioEnabled = false };
        }
        finally
        {
            release.TrySetResult();
        }
        await reconcile;

        Assert.Equal(2, port.StartCount);
        Assert.Equal(2, port.ResetTimingCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HealthyOrTemporarilySuspendedPlaybackDoesNotRestart(bool suspended)
    {
        ChannelId id = CreateChannelId();
        var port = new FakeReceiveSessionPort
        {
            ChannelsValue = [new(id, true, false, suspended)],
            ActiveChannels = [id],
            LivePlaybackChannels = suspended ? [] : [id]
        };
        var controller = new ReceiveSessionController(port);

        await controller.ReconcileAsync();

        Assert.Equal(0, port.StartCount);
        Assert.Equal(0, port.StartWorkCount);
        Assert.Null(port.LastStatus);
    }

    private static ChannelId CreateChannelId()
        => new(new ChannelSessionId("Test", ChannelProtocol.Dmr, 100, 1, "Channel A"));

    private sealed class FakeReceiveSessionPort : IReceiveSessionPort
    {
        public bool IsDisposingValue { get; set; }
        public DateTimeOffset UtcNowValue { get; set; } = DateTimeOffset.UnixEpoch;
        public IReadOnlyList<ReceiveSessionChannelState> ChannelsValue { get; set; } = [];
        public Exception? StartFailure { get; set; }
        public Func<ChannelId, Task>? StartOperation { get; set; }
        public HashSet<ChannelId> ActiveChannels { get; set; } = [];
        public int StartCount { get; private set; }
        public int EnsureDecodeCount { get; private set; }
        public int StartWorkCount { get; private set; }
        public int ResetTimingCount { get; private set; }
        public int ExclusiveCount { get; private set; }
        public SemaphoreSlim? ExclusiveGate { get; init; }
        public string? LastStatus { get; private set; }

        public bool IsDisposing => IsDisposingValue;
        public DateTimeOffset UtcNow => UtcNowValue;
        public IReadOnlyList<ChannelId> LivePlaybackChannels { get; set; } = [];
        public IEnumerable<ReceiveSessionChannelState> Channels => ChannelsValue;

        public bool IsActive(ChannelId channelId) => ActiveChannels.Contains(channelId);
        public bool ShouldEnableLivePlayback(ChannelId channelId, bool isTemporarilySuspended)
            => !isTemporarilySuspended;

        public Task StartAsync(ChannelId channelId, CancellationToken cancellationToken)
        {
            StartCount++;
            return StartFailure is null
                ? StartOperation?.Invoke(channelId) ?? Task.CompletedTask
                : Task.FromException(StartFailure);
        }

        public Task StopAsync(ChannelId channelId, CancellationToken cancellationToken)
        {
            ActiveChannels.Remove(channelId);
            LivePlaybackChannels = LivePlaybackChannels.Where(id => id != channelId).ToArray();
            return Task.CompletedTask;
        }

        public Task EnsureDecodeAsync(ChannelId channelId, CancellationToken cancellationToken)
        {
            EnsureDecodeCount++;
            return Task.CompletedTask;
        }

        public Task SetLivePlaybackEnabledAsync(
            ChannelId channelId,
            bool enabled,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void StartWork(ChannelId channelId) => StartWorkCount++;
        public void ResetTiming(ChannelId channelId) => ResetTimingCount++;
        public void PublishStatus(string text) => LastStatus = text;

        public async Task RunExclusiveAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken)
        {
            ExclusiveCount++;
            if (ExclusiveGate is not null)
                await ExclusiveGate.WaitAsync(cancellationToken);
            try { await operation(cancellationToken); }
            finally { ExclusiveGate?.Release(); }
        }
    }
}
