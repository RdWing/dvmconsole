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
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(20);

    private readonly object sync = new();
    private readonly PatchTransmitSession session;
    private readonly Channel<short[]> frames;
    private readonly Func<CancellationToken, ValueTask> waitForNextFrame;
    private readonly TaskCompletionSource<bool> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task completion;
    private int completionRequested;
    private int queuedFrameCount;

    public PatchTransmitPump(
        PatchTransmitSession session,
        Task? startAfter = null,
        Func<CancellationToken, ValueTask>? waitForNextFrame = null)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.waitForNextFrame = waitForNextFrame ??
            (cancellationToken => new ValueTask(Task.Delay(FrameInterval, cancellationToken)));
        frames = Channel.CreateUnbounded<short[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        completion = RunAsync(startAfter);
    }

    public Task<bool> Started => started.Task;
    public Task Completion => completion;
    public Exception? Failure { get; private set; }

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
                Interlocked.Increment(ref queuedFrameCount);
                if (!frames.Writer.TryWrite(frame))
                {
                    Interlocked.Decrement(ref queuedFrameCount);
                    return false;
                }
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
            if (!await frames.Reader.WaitToReadAsync().ConfigureAwait(false) ||
                !frames.Reader.TryRead(out short[]? firstFrame))
            {
                started.TrySetResult(false);
                return;
            }

            session.Start();
            started.TrySetResult(true);
            Interlocked.Decrement(ref queuedFrameCount);
            await ProcessFrameAsync(firstFrame).ConfigureAwait(false);
            await foreach (short[] frame in frames.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                Interlocked.Decrement(ref queuedFrameCount);
                await ProcessFrameAsync(frame).ConfigureAwait(false);
            }

        }
        catch (Exception exception)
        {
            Failure = exception;
        }
        finally
        {
            started.TrySetResult(false);
            try
            {
                if (session.IsStarted && !session.IsEnded)
                    session.End();
            }
            catch (Exception exception)
            {
                Failure ??= exception;
            }
            session.Dispose();
        }
    }

    private async Task ProcessFrameAsync(short[] frame)
    {
        await waitForNextFrame(CancellationToken.None).ConfigureAwait(false);
        session.Process(frame);
    }
}
