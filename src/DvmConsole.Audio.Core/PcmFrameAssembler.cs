namespace DvmConsole.Audio;

// Converts arbitrary PCM callback sizes into fixed-size voice frames.
public sealed class PcmFrameAssembler
{
    private readonly short[] buffer;
    private int bufferedSamples;

    public PcmFrameAssembler(int frameSize = 160)
    {
        if (frameSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameSize));

        buffer = new short[frameSize];
    }

    public int FrameSize => buffer.Length;
    public int BufferedSamples => bufferedSamples;

    public int Append(ReadOnlySpan<short> samples, Action<ReadOnlyMemory<short>> frameReady)
    {
        ArgumentNullException.ThrowIfNull(frameReady);

        int framesProduced = 0;
        while (!samples.IsEmpty)
        {
            int copyLength = Math.Min(samples.Length, buffer.Length - bufferedSamples);
            samples[..copyLength].CopyTo(buffer.AsSpan(bufferedSamples));
            bufferedSamples += copyLength;
            samples = samples[copyLength..];

            if (bufferedSamples != buffer.Length)
                continue;

            short[] completedFrame = buffer.ToArray();
            bufferedSamples = 0;
            framesProduced++;
            frameReady(completedFrame);
        }

        return framesProduced;
    }

    // Emits the final partial frame with deterministic zero padding. Callers
    // use this at end-of-stream so captured speech is not silently truncated.
    public bool FlushPadded(Action<ReadOnlyMemory<short>> frameReady)
    {
        ArgumentNullException.ThrowIfNull(frameReady);
        if (bufferedSamples == 0)
            return false;

        buffer.AsSpan(bufferedSamples).Clear();
        short[] completedFrame = buffer.ToArray();
        bufferedSamples = 0;
        frameReady(completedFrame);
        return true;
    }

    public void Reset() => bufferedSamples = 0;
}
