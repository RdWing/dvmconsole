using System.Buffers.Binary;
using DvmConsole.Audio;

namespace DvmConsole.Media;

// Applies the legacy TAR silence-trim policy to a completed mono PCM WAV
// without buffering the call in memory or owning its storage location. The
// scan uses 20 ms activity windows, a 400-sample threshold, and retains 120 ms
// of padding around activity.
public static class PcmWavSilenceTrimmer
{
    private const int HeaderLength = 44;
    private const short DefaultSilenceThreshold = 400;
    private const int DefaultWindowSamples = 160;
    private const int DefaultPaddingMilliseconds = 120;

    public static PcmWavTrimResult Trim(
        Stream source,
        Stream destination,
        PcmAudioFormat format,
        short silenceThreshold = DefaultSilenceThreshold,
        int windowSamples = DefaultWindowSamples,
        int paddingMilliseconds = DefaultPaddingMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite || !destination.CanSeek)
        {
            throw new ArgumentException(
                "Silence trimming requires a writable, seekable destination stream.",
                nameof(destination));
        }
        if (ReferenceEquals(source, destination))
        {
            throw new ArgumentException(
                "Silence trimming requires separate source and destination streams.",
                nameof(destination));
        }

        PcmWavTrimAnalysis analysis = Analyze(
            source,
            format,
            silenceThreshold,
            windowSamples,
            paddingMilliseconds);
        WriteRange(
            source,
            destination,
            format,
            analysis.StartSample,
            analysis.Result.OutputSamples);
        return analysis.Result;
    }

    // Computes the legacy trim bounds without modifying the durable source.
    // Finalizers can encode this range directly and retain the WAV for retry.
    public static PcmWavTrimAnalysis Analyze(
        Stream input,
        PcmAudioFormat format,
        short silenceThreshold = DefaultSilenceThreshold,
        int windowSamples = DefaultWindowSamples,
        int paddingMilliseconds = DefaultPaddingMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(format);
        if (!input.CanRead || !input.CanSeek)
        {
            throw new ArgumentException(
                "Silence trimming requires a readable, seekable source stream.",
                nameof(input));
        }
        if (format.BitsPerSample != 16 || format.Channels != 1)
            throw new ArgumentException("Silence trimming requires mono 16-bit PCM.", nameof(format));
        if (silenceThreshold < 0)
            throw new ArgumentOutOfRangeException(nameof(silenceThreshold));
        if (windowSamples <= 0)
            throw new ArgumentOutOfRangeException(nameof(windowSamples));
        if (paddingMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(paddingMilliseconds));

        long dataBytes = ReadDataBytes(input);
        ScanResult scan = Scan(input, dataBytes, silenceThreshold, windowSamples);

        long startSample = 0;
        long endSample = Math.Max(-1, scan.TotalSamples - 1);
        if (scan.FirstActiveSample >= 0)
        {
            long paddingSamples = checked((long)format.SampleRate * paddingMilliseconds / 1000);
            startSample = Math.Max(0, scan.FirstActiveSample - paddingSamples);
            endSample = Math.Min(scan.TotalSamples - 1, scan.LastActiveSample + paddingSamples);
        }

        long outputSamples = endSample >= startSample ? endSample - startSample + 1 : 0;
        return new PcmWavTrimAnalysis(
            startSample,
            new PcmWavTrimResult(
                scan.TotalSamples,
                outputSamples,
                ToMilliseconds(startSample, format.SampleRate),
                ToMilliseconds(Math.Max(0, scan.TotalSamples - endSample - 1), format.SampleRate),
                scan.PeakAmplitude,
                scan.ActiveSampleCount));
    }

    private static ScanResult Scan(
        Stream input,
        long dataBytes,
        short silenceThreshold,
        int windowSamples)
    {
        input.Position = HeaderLength;
        long totalSamples = dataBytes / sizeof(short);
        long firstActiveSample = -1;
        long lastActiveSample = -1;
        long activeSampleCount = 0;
        int peakAmplitude = 0;
        long windowStart = 0;
        int samplesInWindow = 0;
        bool windowHasActivity = false;
        long shiftedWindowStart = totalSamples >= windowSamples
            ? totalSamples % windowSamples
            : 0;
        int samplesInShiftedWindow = 0;
        bool shiftedWindowHasActivity = false;
        byte[] buffer = new byte[64 * 1024];
        int bufferedBytes = 0;
        int bufferOffset = 0;

        for (long sampleIndex = 0; sampleIndex < totalSamples; sampleIndex++)
        {
            if (bufferOffset + sizeof(short) > bufferedBytes)
            {
                int requestedBytes = (int)Math.Min(buffer.Length, (totalSamples - sampleIndex) * sizeof(short));
                bufferedBytes = input.Read(buffer, 0, requestedBytes);
                bufferOffset = 0;
                if (bufferedBytes < sizeof(short))
                    throw new EndOfStreamException("The WAV data chunk ended unexpectedly.");
            }

            short sample = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(bufferOffset, sizeof(short)));
            bufferOffset += sizeof(short);
            int amplitude = Math.Abs((int)sample);
            peakAmplitude = Math.Max(peakAmplitude, amplitude);
            samplesInWindow++;
            if (amplitude >= silenceThreshold)
            {
                activeSampleCount++;
                windowHasActivity = true;
            }

            if (samplesInWindow == windowSamples || sampleIndex == totalSamples - 1)
            {
                if (windowHasActivity)
                {
                    if (firstActiveSample < 0)
                        firstActiveSample = windowStart;
                }

                windowStart = sampleIndex + 1;
                samplesInWindow = 0;
                windowHasActivity = false;
            }

            // The legacy reverse scan starts at totalSamples - windowSamples,
            // so non-window-aligned recordings use a shifted set of windows.
            // Track that alignment in the same pass to preserve its trim edge.
            if (sampleIndex >= shiftedWindowStart)
            {
                samplesInShiftedWindow++;
                if (amplitude >= silenceThreshold)
                    shiftedWindowHasActivity = true;

                if (samplesInShiftedWindow == windowSamples || sampleIndex == totalSamples - 1)
                {
                    if (shiftedWindowHasActivity)
                        lastActiveSample = sampleIndex;
                    samplesInShiftedWindow = 0;
                    shiftedWindowHasActivity = false;
                }
            }
        }

        return new ScanResult(
            totalSamples,
            firstActiveSample,
            lastActiveSample,
            peakAmplitude,
            activeSampleCount);
    }

    private static void WriteRange(
        Stream input,
        Stream output,
        PcmAudioFormat format,
        long startSample,
        long outputSamples)
    {
        input.Position = HeaderLength + checked(startSample * sizeof(short));
        long bytesRemaining = checked(outputSamples * sizeof(short));
        byte[] bytes = new byte[32 * 1024];
        short[] samples = new short[bytes.Length / sizeof(short)];

        using var writer = new PcmWavFileWriter(output, format, leaveOpen: true);
        while (bytesRemaining > 0)
        {
            int requestedBytes = (int)Math.Min(bytes.Length, bytesRemaining);
            int bytesRead = input.Read(bytes, 0, requestedBytes);
            if (bytesRead <= 0 || (bytesRead & 1) != 0)
                throw new EndOfStreamException("The WAV data chunk ended unexpectedly.");

            int sampleCount = bytesRead / sizeof(short);
            for (int index = 0; index < sampleCount; index++)
            {
                samples[index] = BinaryPrimitives.ReadInt16LittleEndian(
                    bytes.AsSpan(index * sizeof(short), sizeof(short)));
            }

            writer.Write(samples.AsSpan(0, sampleCount));
            bytesRemaining -= bytesRead;
        }
    }

    private static long ReadDataBytes(Stream input)
    {
        if (input.Length < HeaderLength)
            throw new InvalidDataException("The WAV file is shorter than its PCM header.");

        Span<byte> header = stackalloc byte[HeaderLength];
        input.Position = 0;
        input.ReadExactly(header);
        if (!header[..4].SequenceEqual("RIFF"u8) ||
            !header[8..12].SequenceEqual("WAVE"u8) ||
            !header[36..40].SequenceEqual("data"u8))
        {
            throw new InvalidDataException("The recording is not a canonical PCM WAV file.");
        }

        long availableBytes = input.Length - HeaderLength;
        long declaredBytes = BinaryPrimitives.ReadUInt32LittleEndian(header[40..]);
        if ((declaredBytes & 1) != 0 || (availableBytes & 1) != 0 || declaredBytes != availableBytes)
        {
            throw new InvalidDataException(
                "The WAV data chunk size does not match the physical PCM data.");
        }

        return declaredBytes;
    }

    private static int ToMilliseconds(long samples, int sampleRate)
        => (int)Math.Round(samples * 1000d / sampleRate, MidpointRounding.ToEven);

    private sealed record ScanResult(
        long TotalSamples,
        long FirstActiveSample,
        long LastActiveSample,
        int PeakAmplitude,
        long ActiveSampleCount);
}

public sealed record PcmWavTrimResult(
    long OriginalSamples,
    long OutputSamples,
    int TrimLeadMs,
    int TrimTailMs,
    int PeakAmplitude,
    long ActiveSampleCount);

public sealed record PcmWavTrimAnalysis(
    long StartSample,
    PcmWavTrimResult Result);
