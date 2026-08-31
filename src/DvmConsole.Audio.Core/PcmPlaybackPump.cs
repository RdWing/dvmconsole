namespace DvmConsole.Audio;

public static class PcmPlaybackPump
{
    public const int RecommendedPrefetchSamples = 1_600;

    public static async Task<bool> RunAsync(
        IAudioPcmStreamReader reader,
        IAudioPlayback playback,
        PcmRateConverter? rateConverter,
        CancellationToken cancellationToken,
        Func<ValueTask>? firstOutputWritten = null,
        IPcmPlaybackPacer? pacer = null,
        ReadOnlyMemory<short> prefetchedSamples = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(playback);
        pacer ??= new RealtimePcmPlaybackPacer(playback.Format);

        bool wroteOutput = false;
        if (!prefetchedSamples.IsEmpty)
            wroteOutput = await WriteAsync(prefetchedSamples).ConfigureAwait(false);

        short[] input = new short[RecommendedPrefetchSamples];
        while (true)
        {
            int sampleCount = await reader.ReadSamplesAsync(input, cancellationToken)
                .ConfigureAwait(false);
            if (sampleCount == 0)
                return wroteOutput;

            wroteOutput = await WriteAsync(input.AsMemory(0, sampleCount)).ConfigureAwait(false) || wroteOutput;
        }

        async ValueTask<bool> WriteAsync(ReadOnlyMemory<short> inputSamples)
        {
            ReadOnlyMemory<short> output = rateConverter is null
                ? inputSamples
                : rateConverter.Convert(inputSamples.Span);
            if (output.IsEmpty)
                return false;

            await pacer.WaitBeforeWriteAsync(cancellationToken).ConfigureAwait(false);
            await playback.WriteAsync(output, cancellationToken).ConfigureAwait(false);
            pacer.ObserveWrittenSamples(output.Length);
            if (!wroteOutput && firstOutputWritten is not null)
                await firstOutputWritten().ConfigureAwait(false);
            return true;
        }
    }
}

public interface IPcmPlaybackPacer
{
    ValueTask WaitBeforeWriteAsync(CancellationToken cancellationToken);
    void ObserveWrittenSamples(int sampleCount);
}

// File and network decoders can produce PCM faster than real time. Pace their
// writes against a monotonic media clock so immediately accepting mixer lanes
// cannot be flooded and discard most of a recording before it is heard.
public sealed class RealtimePcmPlaybackPacer : IPcmPlaybackPacer
{
    private readonly TimeProvider timeProvider;
    private readonly long startedAt;
    private readonly int samplesPerSecond;
    private long writtenSamples;

    public RealtimePcmPlaybackPacer(
        PcmAudioFormat format,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(format);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        startedAt = this.timeProvider.GetTimestamp();
        samplesPerSecond = checked(format.SampleRate * format.Channels);
    }

    public async ValueTask WaitBeforeWriteAsync(CancellationToken cancellationToken)
    {
        TimeSpan mediaElapsed = TimeSpan.FromSeconds(writtenSamples / (double)samplesPerSecond);
        TimeSpan actualElapsed = timeProvider.GetElapsedTime(
            startedAt,
            timeProvider.GetTimestamp());
        TimeSpan remaining = mediaElapsed - actualElapsed;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining, timeProvider, cancellationToken).ConfigureAwait(false);
    }

    public void ObserveWrittenSamples(int sampleCount)
    {
        if (sampleCount < 0)
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        writtenSamples = checked(writtenSamples + sampleCount);
    }
}
