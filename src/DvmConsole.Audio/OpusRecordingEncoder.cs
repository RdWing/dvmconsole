using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;

namespace DvmConsole.Audio;

// Converts TAR PCM to compact Ogg Opus without a platform codec or process.
public static class OpusRecordingEncoder
{
    public const int DefaultBitrate = 9_000;

    public static async Task EncodeWaveFileAsync(
        string wavePath,
        string opusPath,
        int bitrate = DefaultBitrate,
        CancellationToken cancellationToken = default)
        => await EncodeWaveFileAsync(
            wavePath,
            opusPath,
            tags: null,
            bitrate,
            cancellationToken).ConfigureAwait(false);

    public static async Task EncodeWaveFileAsync(
        string wavePath,
        string opusPath,
        IReadOnlyDictionary<string, string>? tags,
        int bitrate = DefaultBitrate,
        CancellationToken cancellationToken = default)
        => await EncodeWaveFileRangeAsync(
            wavePath,
            opusPath,
            startSample: 0,
            sampleCount: null,
            tags,
            bitrate,
            cancellationToken).ConfigureAwait(false);

    public static async Task EncodeWaveFileRangeAsync(
        string wavePath,
        string opusPath,
        long startSample,
        long sampleCount,
        IReadOnlyDictionary<string, string>? tags = null,
        int bitrate = DefaultBitrate,
        CancellationToken cancellationToken = default)
        => await EncodeWaveFileRangeAsync(
            wavePath,
            opusPath,
            startSample,
            (long?)sampleCount,
            tags,
            bitrate,
            cancellationToken).ConfigureAwait(false);

    private static async Task EncodeWaveFileRangeAsync(
        string wavePath,
        string opusPath,
        long startSample,
        long? sampleCount,
        IReadOnlyDictionary<string, string>? tags,
        int bitrate,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wavePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(opusPath);
        if (bitrate <= 0)
            throw new ArgumentOutOfRangeException(nameof(bitrate));
        if (startSample < 0)
            throw new ArgumentOutOfRangeException(nameof(startSample));
        if (sampleCount < 0)
            throw new ArgumentOutOfRangeException(nameof(sampleCount));

        await using var source = new FileStream(
            wavePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16_384,
            useAsync: true);
        await using WavPcmStreamReader pcm = await WavPcmStreamReader.OpenAsync(
            source,
            cancellationToken).ConfigureAwait(false);
        long skipped = await pcm.SkipSamplesAsync(startSample, cancellationToken).ConfigureAwait(false);
        if (skipped != startSample)
            throw new EndOfStreamException("The requested PCM range starts beyond the WAV data.");

        string? directory = Path.GetDirectoryName(opusPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await using var output = new FileStream(
            opusPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 16_384,
            useAsync: false);
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
            output,
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
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        OggOpusDurationFinalizer.SetExactPcmDuration(output, sourceSampleCount, pcm.SampleRate);
        output.Flush(flushToDisk: true);
    }
}
