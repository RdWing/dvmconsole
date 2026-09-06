// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using DvmConsole.Audio;
using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class NativeCapturePumpTests
{
    [Fact]
    public async Task OverlappingStopsRetainTheWorkerUntilItActuallyExits()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var pump = new NativeCapturePump(160,
            () => { entered.Set(); release.Wait(); return 1; },
            _ => 0, () => { }, (_, _) => { });
        pump.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        Task first = pump.StopAsync().AsTask();
        Task second = pump.StopAsync().AsTask();
        try
        {
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => pump.Start());
        }
        finally { release.Set(); }
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(pump.IsRunning);
    }

    [Fact]
    public async Task ConcurrentWakeBurstsDrainSamplesWithoutLosingShutdownWake()
    {
        using var signal = new AutoResetEvent(false);
        var reads = new ConcurrentQueue<int>();
        int published = 0;
        var pump = new NativeCapturePump(
            160,
            () => signal.WaitOne() ? 1 : 0,
            buffer => reads.TryDequeue(out int count) ? count : 0,
            () => signal.Set(),
            (_, count) => Interlocked.Add(ref published, count));
        pump.Start();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, 1_000),
            async (_, _) =>
            {
                reads.Enqueue(1);
                signal.Set();
                await Task.Yield();
            });
        signal.Set();
        await WaitUntilAsync(() => Volatile.Read(ref published) == 1_000);

        var timer = Stopwatch.StartNew();
        await pump.StopAsync();

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1));
        Assert.False(pump.IsRunning);
    }

    [Fact]
    public async Task PumpCanRestartAfterACompleteStop()
    {
        using var signal = new AutoResetEvent(false);
        int pending = 0;
        int published = 0;
        var pump = new NativeCapturePump(
            160,
            () => signal.WaitOne() ? 1 : 0,
            _ => Interlocked.Exchange(ref pending, 0),
            () => signal.Set(),
            (_, count) => Interlocked.Add(ref published, count));

        for (int run = 1; run <= 10; run++)
        {
            pump.Start();
            Volatile.Write(ref pending, 1);
            signal.Set();
            await WaitUntilAsync(() => Volatile.Read(ref published) == run);
            await pump.StopAsync();
        }

        Assert.Equal(10, published);
    }

    [Fact]
    public async Task ImmediateStopRequestWakesABlockedWorkerWithoutWaitingForJoin()
    {
        using var signal = new AutoResetEvent(false);
        var pump = new NativeCapturePump(
            160,
            () => signal.WaitOne() ? 1 : 0,
            _ => 0,
            () => signal.Set(),
            (_, _) => { });
        pump.Start();

        long started = Stopwatch.GetTimestamp();
        pump.RequestStop();
        TimeSpan requestDuration = Stopwatch.GetElapsedTime(started);
        await WaitUntilAsync(() => !pump.IsRunning);
        await pump.StopAsync();

        Assert.True(requestDuration < TimeSpan.FromMilliseconds(100));
        Assert.False(pump.IsRunning);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(1, timeout.Token);
    }
}
