using DvmConsole.Audio;

namespace DvmConsole.Desktop;

// Desktop filesystem adapter for the managed, stream-first recording codecs.
// Portable codec code never learns a path; this host owns file creation,
// sharing, and durable flush policy.
internal static class DesktopRecordingFileCodec
{
    public static OggOpusTagSet ReadOpusTags(string opusPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(opusPath);
        using var source = new FileStream(
            Path.GetFullPath(opusPath),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        return OggOpusTags.Read(source);
    }

    public static Task EncodeWaveAsync(
        string wavePath,
        string opusPath,
        IReadOnlyDictionary<string, string>? tags = null,
        int bitrate = OpusRecordingEncoder.DefaultBitrate,
        CancellationToken cancellationToken = default)
        => EncodeWaveCoreAsync(
            wavePath,
            opusPath,
            startSample: 0,
            sampleCount: null,
            tags,
            bitrate,
            cancellationToken);

    public static Task EncodeWaveRangeAsync(
        string wavePath,
        string opusPath,
        long startSample,
        long sampleCount,
        IReadOnlyDictionary<string, string>? tags = null,
        int bitrate = OpusRecordingEncoder.DefaultBitrate,
        CancellationToken cancellationToken = default)
        => EncodeWaveCoreAsync(
            wavePath,
            opusPath,
            startSample,
            sampleCount,
            tags,
            bitrate,
            cancellationToken);

    private static async Task EncodeWaveCoreAsync(
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
        string fullOutputPath = Path.GetFullPath(opusPath);
        string? directory = Path.GetDirectoryName(fullOutputPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await using var source = new FileStream(
            Path.GetFullPath(wavePath),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16_384,
            useAsync: true);
        await using var output = new FileStream(
            fullOutputPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 16_384,
            useAsync: false);

        if (sampleCount is long rangeLength)
        {
            await OpusRecordingEncoder.EncodeWaveStreamRangeAsync(
                source,
                output,
                startSample,
                rangeLength,
                tags,
                bitrate,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await OpusRecordingEncoder.EncodeWaveStreamAsync(
                source,
                output,
                tags,
                bitrate,
                cancellationToken).ConfigureAwait(false);
        }
        output.Flush(flushToDisk: true);
    }
}
