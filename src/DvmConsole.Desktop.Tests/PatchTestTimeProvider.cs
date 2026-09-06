// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Desktop.Tests;

// Explicit advancement runs due callbacks; it never advances time merely
// because a component schedules a timer.
internal sealed class PatchTestTimeProvider : TimeProvider
{
    private readonly object sync = new();
    private readonly List<ManualTimer> timers = [];
    private long timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref timestamp);
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(GetTimestamp());

    public bool HasTimerWithin(TimeSpan duration)
    {
        lock (sync)
            return timers.Any(timer => timer.IsDueWithin(GetTimestamp() + duration.Ticks));
    }

    public void Advance(TimeSpan duration)
    {
        Interlocked.Add(ref timestamp, duration.Ticks);
        ManualTimer[] due;
        lock (sync)
            due = timers.Where(timer => timer.TakeIfDue(GetTimestamp())).ToArray();
        foreach (ManualTimer timer in due)
            timer.Invoke();
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (sync)
        {
            var timer = new ManualTimer(this, callback, state);
            timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
    }

    private sealed class ManualTimer(PatchTestTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private long due = long.MaxValue;
        private bool disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.sync)
            {
                if (disposed) return false;
                due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.GetTimestamp() + dueTime.Ticks;
                return true;
            }
        }
        public bool IsDueWithin(long timestamp) => !disposed && due <= timestamp;
        public bool TakeIfDue(long now)
        {
            if (disposed || due > now) return false;
            due = long.MaxValue;
            return true;
        }
        public void Invoke() => callback(state);
        public void Dispose()
        {
            lock (owner.sync)
            {
                disposed = true;
                owner.timers.Remove(this);
            }
        }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
