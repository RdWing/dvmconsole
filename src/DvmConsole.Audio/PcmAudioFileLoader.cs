namespace DvmConsole.Audio;

/// <summary>
/// Loads a bounded local alert asset into the 8 kHz mono PCM format expected by
/// the radio transmit sessions. The decoder supports PCM WAV and MPEG audio;
/// other formats require the same explicit DVM_FFMPEG opt-in as web streams.
/// </summary>
public static class PcmAudioFileLoader
{
    private static readonly TimeSpan DefaultMaximumDuration = TimeSpan.FromSeconds(30);

    public static async Task<short[]> LoadAsync(
        string path,
        TimeSpan? maximumDuration = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = System.IO.Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The alert audio file was not found.", fullPath);

        TimeSpan limit = maximumDuration ?? DefaultMaximumDuration;
        if (limit <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumDuration));
        int maximumSamples = checked((int)Math.Ceiling(limit.TotalSeconds * PcmAudioFormat.Voice8KhzMono16Bit.SampleRate));

        await using FileStream source = File.OpenRead(fullPath);
        await using IAudioPcmStreamReader reader = await PcmStreamDecoder.OpenAsync(source, cancellationToken).ConfigureAwait(false);
        var converter = reader.SampleRate == PcmAudioFormat.Voice8KhzMono16Bit.SampleRate
            ? null
            : new PcmRateConverter(reader.SampleRate, PcmAudioFormat.Voice8KhzMono16Bit.SampleRate);
        var output = new List<short>(Math.Min(maximumSamples, 240_000));
        short[] buffer = new short[4_096];
        while (true)
        {
            int count = await reader.ReadSamplesAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
                break;

            short[] converted = converter?.Convert(buffer.AsSpan(0, count)) ?? buffer[..count];
            if (output.Count + converted.Length > maximumSamples)
                throw new InvalidDataException($"Alert audio must be {limit.TotalSeconds:0.#} seconds or shorter.");
            output.AddRange(converted);
        }

        if (output.Count == 0)
            throw new InvalidDataException("The alert audio file contains no samples.");
        return output.ToArray();
    }
}
