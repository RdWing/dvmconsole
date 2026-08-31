namespace DvmConsole.Audio;

// Loads a bounded alert asset into the 8 kHz mono PCM format expected by
// the radio transmit sessions. The managed decoder supports PCM WAV,
// MPEG/MP3, and Ogg Opus audio.
public static class PcmAudioFileLoader
{
    private static readonly TimeSpan DefaultMaximumDuration = TimeSpan.FromSeconds(30);

    // The returned PCM is independent from the supplied stream. The decoder
    // takes ownership of the readable stream and closes it when decoding ends.
    public static async Task<short[]> LoadAsync(
        Stream source,
        TimeSpan? maximumDuration = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException("The audio source must be readable.", nameof(source));

        TimeSpan limit = maximumDuration ?? DefaultMaximumDuration;
        if (limit <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumDuration));
        int maximumSamples = checked((int)Math.Ceiling(limit.TotalSeconds * PcmAudioFormat.Voice8KhzMono16Bit.SampleRate));

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
