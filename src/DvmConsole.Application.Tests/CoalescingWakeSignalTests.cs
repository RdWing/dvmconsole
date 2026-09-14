// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class CoalescingWakeSignalTests
{
    [Fact]
    public async Task FractionalDeadlinesWaitInsteadOfPolling()
    {
        using var signal = new CoalescingWakeSignal();
        // Warm the async path before timing. Truncating these waits to zero
        // reproduces the jitter worker spinning before its release deadline.
        await signal.WaitAsync(TimeSpan.FromMilliseconds(1));
        var elapsed = Stopwatch.StartNew();
        for (int i = 0; i < 40; i++)
            Assert.False(await signal.WaitAsync(TimeSpan.FromMilliseconds(0.5)));
        Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(20));
    }

    [Fact]
    public async Task SignalWakesFiniteAndInfiniteWaitsAndCoalescesBursts()
    {
        using var signal = new CoalescingWakeSignal();
        Assert.True(signal.Set());
        Assert.False(signal.Set());
        Assert.True(await signal.WaitAsync(TimeSpan.Zero));
        Assert.False(await signal.WaitAsync(TimeSpan.Zero));
        foreach (var timeout in new[] { TimeSpan.FromMinutes(1), Timeout.InfiniteTimeSpan })
        {
            var waiting = signal.WaitAsync(timeout).AsTask();
            Assert.False(waiting.IsCompleted);
            Assert.True(signal.Set());
            Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(2)));
        }
    }
}
