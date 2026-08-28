using System.Threading.Channels;
using DvmConsole.Vocoder;

namespace DvmConsole.Media;

// Decouples physical capture callback sizes from the fixed 20 ms cadence used
// by every outbound voice protocol. Some devices, particularly after a cold
// Bluetooth route transition, publish several frames in one callback. Sending
// that callback synchronously would burst multiple protocol packets.
internal sealed class TransmitFramePacer
{
    // Fifty 20 ms frames bound an abnormal live microphone backlog to roughly
    // one second while still allowing ordinary callback bursts to drain.
    internal const int DefaultCapacity = 50;
    private readonly object sync = new();
    private readonly ProcessTransmitSamples processFrame;
    private readonly Action<Exception> publishFault;
    private readonly Func<CancellationToken, ValueTask>? waitForNextFrame;
    private readonly TransmitFrameCadence? cadence;
    private readonly Channel<QueuedFrame> frames;
    private readonly Queue<DateTimeOffset> enqueuedAt = new();
    private readonly TimeProvider timeProvider;
    private readonly CancellationTokenSource cancellation = new();
    private readonly short[] partialFrame = new short[VocoderFrameSizes.PcmSamplesPerFrame];
    private readonly Task completion;
    private int partialSampleCount;
    private int queuedFrameCount;
    private int peakQueuedFrameCount;
    private int faultPublished;
    private bool completed;

    public TransmitFramePacer(
        ProcessTransmitSamples processFrame,
        Action<Exception> publishFault,
        Func<CancellationToken, ValueTask>? waitForNextFrame = null,
        TimeProvider? timeProvider = null,
        int capacity = DefaultCapacity)
    {
        this.processFrame = processFrame ?? throw new ArgumentNullException(nameof(processFrame));
        this.publishFault = publishFault ?? throw new ArgumentNullException(nameof(publishFault));
        this.waitForNextFrame = waitForNextFrame;
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        cadence = waitForNextFrame is null
            ? new TransmitFrameCadence(this.timeProvider)
            : null;
        frames = Channel.CreateBounded<QueuedFrame>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        Capacity = capacity;
        completion = RunAsync();
    }

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

        Exception? overflow = null;
        lock (sync)
        {
            if (completed)
                return false;

            while (!samples.IsEmpty)
            {
                int copyLength = Math.Min(samples.Length, partialFrame.Length - partialSampleCount);
                samples[..copyLength].CopyTo(partialFrame.AsSpan(partialSampleCount));
                partialSampleCount += copyLength;
                samples = samples[copyLength..];

                if (partialSampleCount == partialFrame.Length && !QueuePartialFrame())
                {
                    overflow = FailForOverflow();
                    break;
                }
            }
        }

        if (overflow is null)
            return true;
        PublishFault(overflow);
        return false;
    }

    // Completes normally after every accepted sample has been processed. A
    // final short frame is preserved so the protocol session can perform its
    // existing padding and terminator behavior without losing microphone tail.
    public void Complete()
    {
        Exception? overflow = null;
        lock (sync)
        {
            if (completed)
                return;

            completed = true;
            if (partialSampleCount > 0 && !QueuePartialFrame())
                overflow = FailForOverflow();
            frames.Writer.TryComplete();
        }
        if (overflow is not null)
            PublishFault(overflow);
    }

    private bool QueuePartialFrame()
    {
        var frame = new short[partialSampleCount];
        partialFrame.AsSpan(0, partialSampleCount).CopyTo(frame);
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (!frames.Writer.TryWrite(new QueuedFrame(frame)))
            return false;
        enqueuedAt.Enqueue(now);
        queuedFrameCount++;
        peakQueuedFrameCount = Math.Max(peakQueuedFrameCount, queuedFrameCount);
        partialSampleCount = 0;
        return true;
    }

    private InvalidOperationException FailForOverflow()
    {
        var exception = new InvalidOperationException(
            $"The transmit audio backlog reached its {Capacity}-frame safety limit.");
        Failure = exception;
        completed = true;
        partialSampleCount = 0;
        // Cancel before completing the channel. The worker owns disposal of
        // this source and may finish as soon as the channel is completed.
        cancellation.Cancel();
        frames.Writer.TryComplete(exception);
        return exception;
    }

    private void PublishFault(Exception exception)
    {
        if (Interlocked.Exchange(ref faultPublished, 1) != 0)
            return;
        try
        {
            publishFault(exception);
        }
        catch
        {
            // Fault reporting must not replace the media failure.
        }
    }

    private async Task RunAsync()
    {
        try
        {
            bool firstFrame = true;
            await foreach (QueuedFrame queued in frames.Reader.ReadAllAsync(cancellation.Token).ConfigureAwait(false))
            {
                lock (sync)
                {
                    if (enqueuedAt.Count > 0)
                        enqueuedAt.Dequeue();
                    queuedFrameCount = Math.Max(0, queuedFrameCount - 1);
                }
                if (cadence is not null)
                {
                    await cadence.WaitForNextFrameAsync(cancellation.Token).ConfigureAwait(false);
                }
                else if (!firstFrame)
                {
                    await waitForNextFrame!(cancellation.Token).ConfigureAwait(false);
                }

                processFrame(queued.Samples);
                firstFrame = false;
            }
        }
        catch (OperationCanceledException) when (Failure is not null)
        {
            // A bounded-backlog failure cancels queued stale audio immediately.
        }
        catch (Exception exception)
        {
            Failure ??= exception;
            lock (sync)
            {
                completed = true;
                partialSampleCount = 0;
                frames.Writer.TryComplete(exception);
            }
            try
            {
                PublishFault(Failure!);
            }
            catch
            {
                // PublishFault already isolates observer failures.
            }
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private readonly record struct QueuedFrame(short[] Samples);
}
