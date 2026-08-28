using System.Buffers;
using System.Collections.Concurrent;

namespace DvmConsole.Audio;

// Small streaming PCM rate converter used when a device or decoded media
// exposes a rate different from the 8 kHz voice rate. Downsampling applies a
// low-pass filter before interpolation so frequencies above the destination
// Nyquist limit do not fold into the radio voice band.
public sealed class PcmRateConverter
{
    private static readonly ConcurrentDictionary<(int InputRate, int OutputRate, int TapCount), double[]>
        DownsamplingFilterCache = new();
    private const int MinimumDownsamplingFilterTaps = 63;
    private const int MaximumDownsamplingFilterTaps = 511;
    // Preserve roughly the same transition width as the common 48 kHz to
    // 8 kHz path, which uses 255 taps and adds about 2.7 ms of group delay.
    private const double DownsamplingFilterTapsPerRateRatio = 42.5;
    // Place the 8 kHz cutoff at 3.7 kHz, leaving a transition band before the
    // 4 kHz destination Nyquist limit without trimming ordinary voice audio.
    private const double DownsamplingCutoffFraction = 0.4625;
    // Kaiser beta for approximately 60 dB of stop-band rejection.
    private const double KaiserBeta = 5.65326;

    private readonly int inputRate;
    private readonly int outputRate;
    private readonly int channels;
    private readonly PcmSampleRingBuffer pending = new();
    private readonly double[]? downsamplingCoefficients;
    private readonly short[]? firstFilteredFrame;
    private readonly short[]? secondFilteredFrame;
    private int firstFilteredFrameIndex = -1;
    private int secondFilteredFrameIndex = -1;
    private bool replaceFirstFilteredFrame = true;
    private bool downsamplingInputInitialized;
    // Express the next source position in output-rate units. Advancing by the
    // input rate keeps rational rate pairs deterministic across any chunking.
    private long sourcePositionNumerator;

    public PcmRateConverter(int inputRate, int outputRate, int channels = 1)
    {
        if (inputRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(inputRate));
        if (outputRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(outputRate));
        if (channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(channels));

        this.inputRate = inputRate;
        this.outputRate = outputRate;
        this.channels = channels;
        if (outputRate < inputRate)
        {
            int tapCount = CalculateDownsamplingFilterTapCount(inputRate, outputRate);
            downsamplingCoefficients = DownsamplingFilterCache.GetOrAdd(
                (inputRate, outputRate, tapCount),
                static key => CreateDownsamplingFilter(key.InputRate, key.OutputRate, key.TapCount));
            firstFilteredFrame = new short[channels];
            secondFilteredFrame = new short[channels];
        }
    }

    public short[] Convert(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty)
            return [];
        if (samples.Length % channels != 0)
            throw new ArgumentException("Interleaved PCM must contain complete channel frames.", nameof(samples));
        if (inputRate == outputRate)
            return samples.ToArray();

        int maximumOutputSamples = GetMaximumOutputSampleCount(samples.Length);
        short[] output = ArrayPool<short>.Shared.Rent(maximumOutputSamples);
        try
        {
            int outputSamples = Convert(samples, output);
            return output.AsSpan(0, outputSamples).ToArray();
        }
        finally
        {
            ArrayPool<short>.Shared.Return(output);
        }
    }

    public int GetMaximumOutputSampleCount(int inputSampleCount)
    {
        if (inputSampleCount < 0 || inputSampleCount % channels != 0)
        {
            throw new ArgumentException(
                "Interleaved PCM must contain complete channel frames.",
                nameof(inputSampleCount));
        }
        if (inputRate == outputRate)
            return inputSampleCount;

        int totalFrames = checked((pending.Count + inputSampleCount) / channels);
        long projectedSourcePosition = sourcePositionNumerator;
        if (downsamplingCoefficients is not null &&
            !downsamplingInputInitialized &&
            inputSampleCount > 0)
        {
            int prefixFrames = downsamplingCoefficients.Length - 1;
            totalFrames = checked(totalFrames + prefixFrames);
            projectedSourcePosition = checked((long)prefixFrames * outputRate);
        }

        long availablePositionUnits = checked(
            ((long)(totalFrames - 1) * outputRate) - projectedSourcePosition);
        if (totalFrames < 2 || availablePositionUnits <= 0)
            return 0;

        int outputFrames = checked((int)((availablePositionUnits + inputRate - 1L) / inputRate));
        return checked(outputFrames * channels);
    }

    public int Convert(ReadOnlySpan<short> samples, Span<short> destination)
    {
        if (samples.Length % channels != 0)
            throw new ArgumentException("Interleaved PCM must contain complete channel frames.", nameof(samples));
        if (inputRate == outputRate)
        {
            if (destination.Length < samples.Length)
                throw new ArgumentException("The output buffer is too small.", nameof(destination));
            samples.CopyTo(destination);
            return samples.Length;
        }

        int maximumOutputSamples = GetMaximumOutputSampleCount(samples.Length);
        if (destination.Length < maximumOutputSamples)
            throw new ArgumentException("The output buffer is too small.", nameof(destination));

        AppendInput(samples);

        int outputIndex = 0;
        while (sourcePositionNumerator + outputRate <
               checked((long)(pending.Count / channels) * outputRate))
        {
            int frameIndex = checked((int)(sourcePositionNumerator / outputRate));
            double fraction = (sourcePositionNumerator % outputRate) / (double)outputRate;
            for (int channel = 0; channel < channels; channel++)
            {
                short current = GetSourceSample(frameIndex, channel);
                short next = GetSourceSample(frameIndex + 1, channel);
                double value = current + ((next - current) * fraction);
                destination[outputIndex++] =
                    (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue);
            }
            sourcePositionNumerator = checked(sourcePositionNumerator + inputRate);
        }

        int totalPendingFrames = pending.Count / channels;
        int sourceFrameIndex = checked((int)(sourcePositionNumerator / outputRate));
        int removableFrames = downsamplingCoefficients is null
            ? Math.Min(
                sourceFrameIndex,
                Math.Max(0, totalPendingFrames - 1))
            : Math.Min(
                Math.Max(0, sourceFrameIndex - (downsamplingCoefficients.Length - 1)),
                Math.Max(0, totalPendingFrames - (downsamplingCoefficients.Length - 1)));
        if (removableFrames > 0)
        {
            pending.RemoveFirst(removableFrames * channels);
            sourcePositionNumerator -= checked((long)removableFrames * outputRate);
            InvalidateFilteredFrameCache();
        }

        return outputIndex;
    }

    private void AppendInput(ReadOnlySpan<short> samples)
    {
        if (downsamplingCoefficients is not null &&
            !downsamplingInputInitialized &&
            !samples.IsEmpty)
        {
            int prefixFrames = downsamplingCoefficients.Length - 1;
            pending.EnsureCapacity(checked(
                pending.Count + samples.Length + (prefixFrames * channels)));
            for (int frame = 0; frame < prefixFrames; frame++)
            {
                for (int channel = 0; channel < channels; channel++)
                    pending.Add(samples[channel]);
            }
            sourcePositionNumerator = checked((long)prefixFrames * outputRate);
            downsamplingInputInitialized = true;
        }

        pending.Append(samples);
    }

    private short GetSourceSample(int frameIndex, int channel)
    {
        if (downsamplingCoefficients is null)
            return pending[(frameIndex * channels) + channel];

        if (frameIndex == firstFilteredFrameIndex)
            return firstFilteredFrame![channel];
        if (frameIndex == secondFilteredFrameIndex)
            return secondFilteredFrame![channel];

        short[] target;
        if (replaceFirstFilteredFrame)
        {
            target = firstFilteredFrame!;
            firstFilteredFrameIndex = frameIndex;
        }
        else
        {
            target = secondFilteredFrame!;
            secondFilteredFrameIndex = frameIndex;
        }
        replaceFirstFilteredFrame = !replaceFirstFilteredFrame;
        FilterFrame(frameIndex, target);
        return target[channel];
    }

    private void FilterFrame(int frameIndex, Span<short> destination)
    {
        double[] coefficients = downsamplingCoefficients!;
        int center = coefficients.Length / 2;
        int oldestFrame = frameIndex - (coefficients.Length - 1);

        for (int channel = 0; channel < channels; channel++)
        {
            double filtered = 0;
            for (int tap = 0; tap < center; tap++)
            {
                int recentFrame = frameIndex - tap;
                int pairedOldestFrame = oldestFrame + tap;
                filtered += coefficients[tap] *
                    (pending[(recentFrame * channels) + channel] +
                     pending[(pairedOldestFrame * channels) + channel]);
            }

            filtered += coefficients[center] *
                pending[((frameIndex - center) * channels) + channel];
            destination[channel] = (short)Math.Clamp(
                Math.Round(filtered),
                short.MinValue,
                short.MaxValue);
        }
    }

    private void InvalidateFilteredFrameCache()
    {
        firstFilteredFrameIndex = -1;
        secondFilteredFrameIndex = -1;
        replaceFirstFilteredFrame = true;
    }

    private static int CalculateDownsamplingFilterTapCount(int inputRate, int outputRate)
    {
        double scaledTapCount = DownsamplingFilterTapsPerRateRatio * inputRate / outputRate;
        int tapCount = scaledTapCount >= MaximumDownsamplingFilterTaps
            ? MaximumDownsamplingFilterTaps
            : Math.Max(MinimumDownsamplingFilterTaps, (int)Math.Ceiling(scaledTapCount));
        if ((tapCount & 1) != 0)
            return tapCount;
        return tapCount < MaximumDownsamplingFilterTaps ? tapCount + 1 : tapCount - 1;
    }

    private static double[] CreateDownsamplingFilter(
        int inputRate,
        int outputRate,
        int tapCount)
    {
        var coefficients = new double[tapCount];
        int center = tapCount / 2;
        double cutoff = DownsamplingCutoffFraction * outputRate / inputRate;
        double denominator = ModifiedBesselI0(KaiserBeta);
        double sum = 0;
        for (int tap = 0; tap < tapCount; tap++)
        {
            int offset = tap - center;
            double ideal = offset == 0
                ? 2 * cutoff
                : Math.Sin(2 * Math.PI * cutoff * offset) / (Math.PI * offset);
            double position = offset / (double)center;
            double window = ModifiedBesselI0(
                KaiserBeta * Math.Sqrt(Math.Max(0, 1 - (position * position)))) /
                denominator;
            coefficients[tap] = ideal * window;
            sum += coefficients[tap];
        }

        for (int tap = 0; tap < coefficients.Length; tap++)
            coefficients[tap] /= sum;
        return coefficients;
    }

    private static double ModifiedBesselI0(double value)
    {
        double halfSquared = value * value / 4;
        double sum = 1;
        double term = 1;
        for (int index = 1; index <= 32; index++)
        {
            term *= halfSquared / (index * index);
            sum += term;
            if (term <= sum * 1e-16)
                break;
        }
        return sum;
    }
}

// Provides O(1) removal from the front of the streaming sample window. A
// List<T>.RemoveRange shifted the retained FIR history on every input chunk.
internal sealed class PcmSampleRingBuffer
{
    private const int InitialCapacity = 256;
    private short[] samples = new short[InitialCapacity];
    private int head;

    public int Count { get; private set; }

    public short this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            int physicalIndex = head + index;
            if (physicalIndex >= samples.Length)
                physicalIndex -= samples.Length;
            return samples[physicalIndex];
        }
    }

    public void Add(short sample)
    {
        EnsureCapacity(checked(Count + 1));
        int tail = head + Count;
        if (tail >= samples.Length)
            tail -= samples.Length;
        samples[tail] = sample;
        Count++;
    }

    public void Append(ReadOnlySpan<short> values)
    {
        if (values.IsEmpty)
            return;
        EnsureCapacity(checked(Count + values.Length));
        int tail = head + Count;
        if (tail >= samples.Length)
            tail -= samples.Length;
        int firstLength = Math.Min(values.Length, samples.Length - tail);
        values[..firstLength].CopyTo(samples.AsSpan(tail));
        values[firstLength..].CopyTo(samples);
        Count += values.Length;
    }

    public void RemoveFirst(int count)
    {
        if (count < 0 || count > Count)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0)
            return;

        head += count;
        if (head >= samples.Length)
            head -= samples.Length;
        Count -= count;
        if (Count == 0)
            head = 0;
    }

    public void EnsureCapacity(int capacity)
    {
        if (capacity <= samples.Length)
            return;

        int expandedCapacity = Math.Max(capacity, checked(samples.Length * 2));
        var expanded = new short[expandedCapacity];
        int firstLength = Math.Min(Count, samples.Length - head);
        samples.AsSpan(head, firstLength).CopyTo(expanded);
        samples.AsSpan(0, Count - firstLength).CopyTo(expanded.AsSpan(firstLength));
        samples = expanded;
        head = 0;
    }
}
