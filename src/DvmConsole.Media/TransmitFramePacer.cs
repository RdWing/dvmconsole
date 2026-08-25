using System.Threading.Channels;
using DvmConsole.Vocoder;

namespace DvmConsole.Media;

// Decouples physical capture callback sizes from the fixed 20 ms cadence used
// by every outbound voice protocol. Some devices, particularly after a cold
// Bluetooth route transition, publish several frames in one callback. Sending
// that callback synchronously would burst multiple protocol packets.
internal sealed class TransmitFramePacer
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(20);

    private readonly object sync = new();
    private readonly ProcessTransmitSamples processFrame;
    private readonly Action<Exception> publishFault;
    private readonly Func<CancellationToken, ValueTask>? waitForNextFrame;
    private readonly TimeProvider timeProvider;
    private readonly Channel<short[]> frames;
    private readonly short[] partialFrame = new short[VocoderFrameSizes.PcmSamplesPerFrame];
    private readonly Task completion;
    private int partialSampleCount;
    private long lastFrameStartedTimestamp;
    private bool completed;

    public TransmitFramePacer(
        ProcessTransmitSamples processFrame,
        Action<Exception> publishFault,
        Func<CancellationToken, ValueTask>? waitForNextFrame = null,
        TimeProvider? timeProvider = null)
    {
        this.processFrame = processFrame ?? throw new ArgumentNullException(nameof(processFrame));
        this.publishFault = publishFault ?? throw new ArgumentNullException(nameof(publishFault));
        this.waitForNextFrame = waitForNextFrame;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        frames = Channel.CreateUnbounded<short[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        completion = RunAsync();
    }

    public Task Completion => completion;
    public Exception? Failure { get; private set; }

    public bool Enqueue(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty)
            return false;

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

                if (partialSampleCount == partialFrame.Length)
                    QueuePartialFrame();
            }
            return true;
        }
    }

    // Completes normally after every accepted sample has been processed. A
    // final short frame is preserved so the protocol session can perform its
    // existing padding and terminator behavior without losing microphone tail.
    public void Complete()
    {
        lock (sync)
        {
            if (completed)
                return;

            completed = true;
            if (partialSampleCount > 0)
                QueuePartialFrame();
            frames.Writer.TryComplete();
        }
    }

    private void QueuePartialFrame()
    {
        var frame = new short[partialSampleCount];
        partialFrame.AsSpan(0, partialSampleCount).CopyTo(frame);
        if (!frames.Writer.TryWrite(frame))
            throw new InvalidOperationException("The transmit frame queue stopped accepting audio.");
        partialSampleCount = 0;
    }

    private async Task RunAsync()
    {
        try
        {
            bool firstFrame = true;
            await foreach (short[] frame in frames.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (!firstFrame)
                {
                    if (waitForNextFrame is not null)
                        await waitForNextFrame(CancellationToken.None).ConfigureAwait(false);
                    else
                        await WaitForCadenceAsync().ConfigureAwait(false);
                }

                lastFrameStartedTimestamp = timeProvider.GetTimestamp();
                processFrame(frame);
                firstFrame = false;
            }
        }
        catch (Exception exception)
        {
            Failure = exception;
            lock (sync)
            {
                completed = true;
                partialSampleCount = 0;
                frames.Writer.TryComplete(exception);
            }
            try
            {
                publishFault(exception);
            }
            catch
            {
                // Fault reporting must not replace the original media failure.
            }
        }
    }

    private async ValueTask WaitForCadenceAsync()
    {
        TimeSpan elapsed = timeProvider.GetElapsedTime(
            lastFrameStartedTimestamp,
            timeProvider.GetTimestamp());
        TimeSpan remaining = FrameInterval - elapsed;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, timeProvider, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
