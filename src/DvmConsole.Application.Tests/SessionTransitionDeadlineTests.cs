// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class SessionTransitionDeadlineTests
{
    [Fact]
    public async Task ExpiredDeadlineStillStartsIndependentCleanupWithCancelledAdmission()
    {
        var time = new ControlledTimeProvider();
        using var deadline = new SessionTransitionDeadline(TimeSpan.FromSeconds(1), time);
        var cleanup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        time.Expire();
        await Assert.ThrowsAsync<TimeoutException>(() => deadline.RunAsync(token =>
        {
            cleanup.SetResult(token.IsCancellationRequested);
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, CancellationToken.None));
        Assert.True(await cleanup.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AllTransitionStepsShareOneDeadline()
    {
        var time = new ControlledTimeProvider();
        using var deadline = new SessionTransitionDeadline(TimeSpan.FromSeconds(1), time);
        await deadline.RunAsync(_ => Task.CompletedTask, CancellationToken.None);
        time.Expire();
        using var gate = new SemaphoreSlim(1);
        await Assert.ThrowsAsync<TimeoutException>(() => deadline.WaitAsync(gate, CancellationToken.None));
        Assert.Equal(1, gate.CurrentCount);
    }

    private sealed class ControlledTimeProvider : TimeProvider
    {
        private TimerCallback? callback;
        private object? state;
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            this.callback = callback;
            this.state = state;
            return new TimerRegistration(() => this.callback = null);
        }
        public void Expire()
        {
            timestamp += TimeSpan.TicksPerSecond;
            callback?.Invoke(state);
        }
        private sealed class TimerRegistration(Action dispose) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() => dispose();
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
