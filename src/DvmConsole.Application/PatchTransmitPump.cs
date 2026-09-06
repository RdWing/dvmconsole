// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Threading.Channels;
using DvmConsole.Audio;
using DvmConsole.Media;
using DvmConsole.Vocoder;

namespace DvmConsole.Application;

// Feeds patch PCM to the normal protocol transmit session at the same 20 ms
// cadence as microphone capture. Receive decoders deliver protocol-sized
// batches (for example, nine P25 frames at once), so processing a whole batch
// synchronously would burst several destination packets onto the network.
internal sealed class PatchTransmitPump
{
    internal const int DefaultCapacity = 250;

    private readonly object sync = new();
    private readonly PatchTransmitSession session;
    private readonly Channel<QueuedFrame> frames;
    private readonly Queue<long> enqueuedAt = new();
    private readonly TimeSpan? maximumQueuedAge;
    private readonly ITimer? backlogTimer;
    private readonly Func<CancellationToken, ValueTask> waitForNextFrame;
    private readonly TimeProvider timeProvider;
    private readonly CancellationTokenSource cancellation = new();
    private readonly TaskCompletionSource<bool> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task completion;
    private readonly BoundedPcmBufferPool framePool;
    private int completionRequested;
    private int queuedFrameCount;
    private int peakQueuedFrameCount;
    private bool workerFinished;
    private bool releasing;

    public PatchTransmitPump(
        PatchTransmitSession session,
        Task? startAfter = null,
        Func<TimeSpan, CancellationToken, ValueTask>? delay = null,
        TimeProvider? timeProvider = null,
        int capacity = DefaultCapacity,
        TimeSpan? maximumQueuedAge = null)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        if (maximumQueuedAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumQueuedAge));
        this.maximumQueuedAge = maximumQueuedAge;
        if (maximumQueuedAge is not null)
            backlogTimer = this.timeProvider.CreateTimer(
                _ => ExpireStaleAudio(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        TransmitFrameCadence cadence =
            TransmitFrameCadence.StartAfterFrameInterval(this.timeProvider, delay);
        waitForNextFrame = cadence.WaitForNextFrameAsync;
        frames = Channel.CreateBounded<QueuedFrame>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        Capacity = capacity;
        framePool = new BoundedPcmBufferPool(
            VocoderFrameSizes.PcmSamplesPerFrame,
            Math.Min(capacity, 64));
        completion = RunAsync(startAfter);
    }

    public Task<bool> Started => started.Task;
    public Task Completion => completion;
    public Exception? Failure { get; private set; }
    public int Capacity { get; }
    public bool WasOverloaded { get; private set; }

    public TransmitQueueHealth CaptureHealth()
    {
        lock (sync)
        {
            TimeSpan? oldestAge = enqueuedAt.Count == 0
                ? null
                : timeProvider.GetElapsedTime(enqueuedAt.Peek());
            return new TransmitQueueHealth(
                queuedFrameCount,
                peakQueuedFrameCount,
                oldestAge,
                Capacity);
        }
    }

    public bool Enqueue(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty)
            return false;
        if (samples.Length % VocoderFrameSizes.PcmSamplesPerFrame != 0)
            throw new ArgumentException("Patch audio must contain complete 20 ms PCM frames.", nameof(samples));

        lock (sync)
        {
            if (completionRequested != 0)
                return false;

            for (int offset = 0; offset < samples.Length; offset += VocoderFrameSizes.PcmSamplesPerFrame)
            {
                short[] frame = framePool.Rent();
                samples.Slice(offset, frame.Length).CopyTo(frame);
                long now = timeProvider.GetTimestamp();
                if (!frames.Writer.TryWrite(new QueuedFrame(frame, now)))
                {
                    framePool.Return(frame);
                    var exception = new PatchBacklogException(
                        $"The patch transmit backlog reached its {Capacity}-frame safety limit.");
                    WasOverloaded = true;
                    Failure = exception;
                    completionRequested = 1;
                    // Cancel before completing the channel. The worker owns
                    // disposal and may finish immediately after completion.
                    cancellation.Cancel();
                    frames.Writer.TryComplete(exception);
                    throw exception;
                }
                enqueuedAt.Enqueue(now);
                queuedFrameCount++;
                peakQueuedFrameCount = Math.Max(peakQueuedFrameCount, queuedFrameCount);
                if (queuedFrameCount == 1)
                    ScheduleExpiration();
            }
            return true;
        }
    }

    public void Complete()
    {
        lock (sync)
        {
            if (completionRequested != 0)
                return;
            completionRequested = 1;
            frames.Writer.TryComplete();
        }
    }

    // Final owner shutdown must not key a waiting replacement call or play
    // seconds of stale PCM. The worker still completes the current protocol
    // packet and terminator before releasing its session.
    public void DiscardPendingAudioAndComplete()
    {
        lock (sync)
        {
            if (workerFinished)
                return;
            completionRequested = 1;
            cancellation.Cancel();
            frames.Writer.TryComplete();
        }
    }

    private async Task RunAsync(Task? startAfter)
    {
        try
        {
            if (startAfter is not null)
                await startAfter.WaitAsync(cancellation.Token).ConfigureAwait(false);
            if (!await frames.Reader.WaitToReadAsync(cancellation.Token).ConfigureAwait(false) ||
                !frames.Reader.TryRead(out QueuedFrame firstFrame))
            {
                started.TrySetResult(false);
                return;
            }

            MarkDequeued();
            await ProcessFrameAndReturnAsync(firstFrame).ConfigureAwait(false);
            await foreach (QueuedFrame frame in frames.Reader.ReadAllAsync(cancellation.Token).ConfigureAwait(false))
            {
                MarkDequeued();
                await ProcessFrameAndReturnAsync(frame).ConfigureAwait(false);
            }

        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Final shutdown and bounded-backlog failure discard pending PCM.
        }
        catch (Exception exception)
        {
            Failure ??= exception;
        }
        finally
        {
            lock (sync)
            {
                completionRequested = 1;
                releasing = true;
                backlogTimer?.Dispose();
                frames.Writer.TryComplete();
            }
            while (frames.Reader.TryRead(out QueuedFrame abandoned))
            {
                MarkDequeued();
                framePool.Return(abandoned.Samples);
            }
            started.TrySetResult(false);
            try
            {
                if (session.IsStarted && !session.IsEnded)
                    await session.EndAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Failure = Failure is null ? exception
                    : new AggregateException("Patch cleanup failed after an earlier forwarding failure.", Failure, exception);
            }
            try
            {
                session.Dispose();
            }
            catch (Exception exception)
            {
                Failure = Failure is null ? exception
                    : new AggregateException("Patch cleanup failed after an earlier forwarding failure.", Failure, exception);
            }
            finally
            {
                lock (sync)
                {
                    workerFinished = true;
                    cancellation.Dispose();
                }
            }
        }
    }

    private void MarkDequeued()
    {
        lock (sync)
        {
            if (enqueuedAt.Count > 0)
                enqueuedAt.Dequeue();
            queuedFrameCount = Math.Max(0, queuedFrameCount - 1);
            if (completionRequested == 0 || !cancellation.IsCancellationRequested)
                ScheduleExpiration();
        }
    }

    private void ScheduleExpiration()
    {
        if (maximumQueuedAge is not TimeSpan limit || releasing || workerFinished)
            return;
        TimeSpan remaining = enqueuedAt.Count == 0
            ? Timeout.InfiniteTimeSpan
            : TimeSpan.FromTicks(Math.Max(0, (limit - timeProvider.GetElapsedTime(enqueuedAt.Peek())).Ticks));
        backlogTimer?.Change(remaining, Timeout.InfiniteTimeSpan);
    }

    private void ExpireStaleAudio()
    {
        lock (sync)
        {
            if (workerFinished || releasing || cancellation.IsCancellationRequested || enqueuedAt.Count == 0)
                return;
            if (timeProvider.GetElapsedTime(enqueuedAt.Peek()) < maximumQueuedAge!.Value)
            {
                ScheduleExpiration();
                return;
            }
            FailStaleAudio();
        }
    }

    private void FailStaleAudio()
    {
        WasOverloaded = true;
        Failure ??= new PatchBacklogException(
            "Patch audio exceeded the one-second queue-age limit; stale unencoded audio was discarded.");
        completionRequested = 1;
        cancellation.Cancel();
        frames.Writer.TryComplete();
    }

    private async Task ProcessFrameAsync(QueuedFrame frame)
    {
        await waitForNextFrame(cancellation.Token).ConfigureAwait(false);
        ThrowIfFrameExpired(frame);
        session.Process(frame.Samples);
    }

    private void ThrowIfFrameExpired(QueuedFrame frame)
    {
        lock (sync)
        {
            if (maximumQueuedAge is TimeSpan limit &&
                timeProvider.GetElapsedTime(frame.EnqueuedAt) >= limit && !cancellation.IsCancellationRequested)
                FailStaleAudio();
        }
        cancellation.Token.ThrowIfCancellationRequested();
    }

    private async Task ProcessFrameAndReturnAsync(QueuedFrame frame)
    {
        try
        {
            ThrowIfFrameExpired(frame);
            if (!session.IsStarted)
            {
                session.Start();
                started.TrySetResult(true);
            }
            await ProcessFrameAsync(frame).ConfigureAwait(false);
        }
        finally
        {
            framePool.Return(frame.Samples);
        }
    }

    private readonly record struct QueuedFrame(short[] Samples, long EnqueuedAt);
}
