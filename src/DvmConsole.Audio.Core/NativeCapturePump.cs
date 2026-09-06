// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.ExceptionServices;

namespace DvmConsole.Audio;

// Shared lifecycle for native capture APIs that expose wait, drain, and wake.
// Native start/stop remains with the platform endpoint that owns the handle.
internal sealed class NativeCapturePump
{
    private readonly object sync = new();
    private readonly int bufferSamples;
    private readonly Func<int> waitForCapture;
    private readonly Func<short[], int> readCapture;
    private readonly Action wakeCapture;
    private readonly Action<short[], int> publishSamples;
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private Task? stopping;

    public NativeCapturePump(
        int bufferSamples,
        Func<int> waitForCapture,
        Func<short[], int> readCapture,
        Action wakeCapture,
        Action<short[], int> publishSamples)
    {
        if (bufferSamples <= 0)
            throw new ArgumentOutOfRangeException(nameof(bufferSamples));
        this.bufferSamples = bufferSamples;
        this.waitForCapture = waitForCapture ?? throw new ArgumentNullException(nameof(waitForCapture));
        this.readCapture = readCapture ?? throw new ArgumentNullException(nameof(readCapture));
        this.wakeCapture = wakeCapture ?? throw new ArgumentNullException(nameof(wakeCapture));
        this.publishSamples = publishSamples ?? throw new ArgumentNullException(nameof(publishSamples));
    }

    public bool IsRunning
    {
        get
        {
            lock (sync)
                return worker is { IsCompleted: false };
        }
    }

    public bool HasStarted
    {
        get
        {
            lock (sync)
                return cancellation is not null;
        }
    }

    public void Start()
    {
        lock (sync)
        {
            if (stopping is { IsCompleted: false })
                throw new InvalidOperationException("Native capture is still stopping.");
            stopping = null;
            if (worker is not null)
            {
                if (!worker.IsCompleted)
                    return;
                Exception? failure = worker.Exception?.GetBaseException();
                throw new InvalidOperationException(
                    "The native capture pump must be stopped before it can restart.",
                    failure);
            }

            var nextCancellation = new CancellationTokenSource();
            cancellation = nextCancellation;
            worker = Task.Factory.StartNew(
                () => Run(nextCancellation.Token),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        Task completion;
        lock (sync)
            completion = stopping ??= StopCoreAsync();
        // Endpoint disposal callers join the same operation. Caller cancellation
        // is reported only after native ownership is safely released.
        await completion.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task StopCoreAsync()
    {
        CancellationTokenSource? currentCancellation = cancellation;
        Task? currentWorker = worker;
        if (currentCancellation is null)
            return;
        Exception? failure = null;
        try
        {
            currentCancellation.Cancel();
            try { wakeCapture(); }
            catch (Exception exception) { failure = exception; }
            if (currentWorker is not null)
                await currentWorker.ConfigureAwait(false);
        }
        finally
        {
            lock (sync)
            {
                cancellation = null;
                worker = null;
            }
            currentCancellation.Dispose();
        }
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public void RequestStop()
    {
        CancellationTokenSource? currentCancellation;
        lock (sync)
            currentCancellation = cancellation;
        if (currentCancellation is null)
            return;

        try
        {
            currentCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        wakeCapture();
    }

    private void Run(CancellationToken cancellationToken)
    {
        var buffer = new short[bufferSamples];
        while (!cancellationToken.IsCancellationRequested)
        {
            if (waitForCapture() == 0)
                continue;

            while (!cancellationToken.IsCancellationRequested)
            {
                int count = readCapture(buffer);
                if (count == 0)
                    break;
                if ((uint)count > (uint)buffer.Length)
                {
                    throw new InvalidOperationException(
                        $"Native capture returned {count} samples for a {buffer.Length}-sample buffer.");
                }
                publishSamples(buffer, count);
            }
        }
    }
}
