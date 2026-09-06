// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Audio;

// The frame is borrowed from the assembler and is valid only for the duration
// of the callback. This keeps synchronous media paths allocation-free without
// changing the existing owned-memory handoff contract.
public delegate void BorrowedPcmFrameHandler(ReadOnlySpan<short> frame);

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

    public int AppendBorrowed(ReadOnlySpan<short> samples, BorrowedPcmFrameHandler frameReady)
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

            bufferedSamples = 0;
            framesProduced++;
            frameReady(buffer);
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

    public bool FlushPaddedBorrowed(BorrowedPcmFrameHandler frameReady)
    {
        ArgumentNullException.ThrowIfNull(frameReady);
        if (bufferedSamples == 0)
            return false;

        buffer.AsSpan(bufferedSamples).Clear();
        bufferedSamples = 0;
        frameReady(buffer);
        return true;
    }

    public void Reset() => bufferedSamples = 0;
}
