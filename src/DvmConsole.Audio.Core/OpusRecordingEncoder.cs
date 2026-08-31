using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;

namespace DvmConsole.Audio;

// Converts TAR PCM to compact Ogg Opus without a platform codec or process.
public static class OpusRecordingEncoder
{
    public const int DefaultBitrate = 9_000;

    public static async Task EncodeWaveStreamAsync(
        Stream waveSource,
        Stream opusOutput,
        IReadOnlyDictionary<string, string>? tags = null,
        int bitrate = DefaultBitrate,
        CancellationToken cancellationToken = default)
        => await EncodeWaveStreamRangeAsync(
            waveSource,
            opusOutput,
            startSample: 0,
            sampleCount: null,
            tags,
            bitrate,
            cancellationToken).ConfigureAwait(false);

    public static async Task EncodeWaveStreamRangeAsync(
        Stream waveSource,
        Stream opusOutput,
        long startSample,
        long sampleCount,
        IReadOnlyDictionary<string, string>? tags = null,
        int bitrate = DefaultBitrate,
        CancellationToken cancellationToken = default)
        => await EncodeWaveStreamRangeAsync(
            waveSource,
            opusOutput,
            startSample,
            (long?)sampleCount,
            tags,
            bitrate,
            cancellationToken).ConfigureAwait(false);

    private static async Task EncodeWaveStreamRangeAsync(
        Stream waveSource,
        Stream opusOutput,
        long startSample,
        long? sampleCount,
        IReadOnlyDictionary<string, string>? tags,
        int bitrate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(waveSource);
        ArgumentNullException.ThrowIfNull(opusOutput);
        if (!waveSource.CanRead)
            throw new ArgumentException("The WAV source must be readable.", nameof(waveSource));
        if (!opusOutput.CanWrite || !opusOutput.CanSeek)
        {
            throw new ArgumentException(
                "The Opus destination must be writable and seekable.",
                nameof(opusOutput));
        }
        if (bitrate <= 0)
            throw new ArgumentOutOfRangeException(nameof(bitrate));
        if (startSample < 0)
            throw new ArgumentOutOfRangeException(nameof(startSample));
        if (sampleCount < 0)
            throw new ArgumentOutOfRangeException(nameof(sampleCount));

        await using WavPcmStreamReader pcm = await WavPcmStreamReader.OpenAsync(
            waveSource,
            leaveOpen: true,
            cancellationToken).ConfigureAwait(false);
        long skipped = await pcm.SkipSamplesAsync(startSample, cancellationToken).ConfigureAwait(false);
        if (skipped != startSample)
            throw new EndOfStreamException("The requested PCM range starts beyond the WAV data.");

        opusOutput.SetLength(0);
        opusOutput.Position = 0;
        using IOpusEncoder encoder = OpusCodecFactory.CreateEncoder(
            pcm.SampleRate,
            1,
            OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = bitrate;
        encoder.UseVBR = true;

        var opusTags = new OpusTags { Comment = "DVM Console TAR" };
        if (tags is not null)
        {
            foreach ((string name, string value) in tags)
            {
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrEmpty(value))
                    opusTags.Fields[name] = value;
            }
        }
        var ogg = new OpusOggWriteStream(
            encoder,
            opusOutput,
            opusTags,
            inputSampleRate: pcm.SampleRate,
            leaveOpen: true);
        short[] samples = new short[1600];
        long sourceSampleCount = 0;
        while (sampleCount is null || sourceSampleCount < sampleCount.Value)
        {
            int requested = sampleCount is null
                ? samples.Length
                : (int)Math.Min(samples.Length, sampleCount.Value - sourceSampleCount);
            int count = await pcm.ReadSamplesAsync(
                samples.AsMemory(0, requested),
                cancellationToken).ConfigureAwait(false);
            if (count == 0)
                break;
            ogg.WriteSamples(samples, 0, count);
            sourceSampleCount = checked(sourceSampleCount + count);
        }
        if (sampleCount is long expectedSamples && sourceSampleCount != expectedSamples)
            throw new EndOfStreamException("The WAV data ended before the requested PCM range.");
        ogg.Finish();
        await opusOutput.FlushAsync(cancellationToken).ConfigureAwait(false);
        OggOpusDurationFinalizer.SetExactPcmDuration(
            opusOutput,
            sourceSampleCount,
            pcm.SampleRate);
        await opusOutput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
