using System.Threading.Channels;
using DvmConsole.Media;
using DvmConsole.Vocoder;

namespace DvmConsole.Desktop;

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
    private readonly Queue<DateTimeOffset> enqueuedAt = new();
    private readonly Func<CancellationToken, ValueTask> waitForNextFrame;
    private readonly TimeProvider timeProvider;
    private readonly CancellationTokenSource cancellation = new();
    private readonly TaskCompletionSource<bool> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task completion;
    private int completionRequested;
    private int queuedFrameCount;
    private int peakQueuedFrameCount;

    public PatchTransmitPump(
        PatchTransmitSession session,
        Task? startAfter = null,
        Func<CancellationToken, ValueTask>? waitForNextFrame = null,
        TimeProvider? timeProvider = null,
        int capacity = DefaultCapacity)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        var cadence = new TransmitFrameCadence(this.timeProvider, delayFirstFrame: true);
        this.waitForNextFrame = waitForNextFrame ?? cadence.WaitForNextFrameAsync;
        frames = Channel.CreateBounded<QueuedFrame>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        Capacity = capacity;
        completion = RunAsync(startAfter);
    }

    public Task<bool> Started => started.Task;
    public Task Completion => completion;
    public Exception? Failure { get; private set; }
    public int Capacity { get; }

    public TransmitQueueHealth CaptureHealth()
    {
        lock (sync)
        {
            TimeSpan? oldestAge = enqueuedAt.Count == 0
                ? null
                : timeProvider.GetUtcNow() - enqueuedAt.Peek();
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
                var frame = new short[VocoderFrameSizes.PcmSamplesPerFrame];
                samples.Slice(offset, frame.Length).CopyTo(frame);
                DateTimeOffset now = timeProvider.GetUtcNow();
                if (!frames.Writer.TryWrite(new QueuedFrame(frame)))
                {
                    var exception = new InvalidOperationException(
                        $"The patch transmit backlog reached its {Capacity}-frame safety limit.");
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

    private async Task RunAsync(Task? startAfter)
    {
        try
        {
            if (startAfter is not null)
                await startAfter.ConfigureAwait(false);
            if (!await frames.Reader.WaitToReadAsync(cancellation.Token).ConfigureAwait(false) ||
                !frames.Reader.TryRead(out QueuedFrame firstFrame))
            {
                started.TrySetResult(false);
                return;
            }

            session.Start();
            started.TrySetResult(true);
            MarkDequeued();
            await ProcessFrameAsync(firstFrame.Samples).ConfigureAwait(false);
            await foreach (QueuedFrame frame in frames.Reader.ReadAllAsync(cancellation.Token).ConfigureAwait(false))
            {
                MarkDequeued();
                await ProcessFrameAsync(frame.Samples).ConfigureAwait(false);
            }

        }
        catch (OperationCanceledException) when (Failure is not null)
        {
            // A bounded-backlog failure discards queued stale patch audio.
        }
        catch (Exception exception)
        {
            Failure ??= exception;
        }
        finally
        {
            started.TrySetResult(false);
            try
            {
                if (session.IsStarted && !session.IsEnded)
                    await session.EndAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Failure ??= exception;
            }
            session.Dispose();
            cancellation.Dispose();
        }
    }

    private void MarkDequeued()
    {
        lock (sync)
        {
            if (enqueuedAt.Count > 0)
                enqueuedAt.Dequeue();
            queuedFrameCount = Math.Max(0, queuedFrameCount - 1);
        }
    }

    private async Task ProcessFrameAsync(short[] frame)
    {
        await waitForNextFrame(cancellation.Token).ConfigureAwait(false);
        session.Process(frame);
    }

    private readonly record struct QueuedFrame(short[] Samples);
}
