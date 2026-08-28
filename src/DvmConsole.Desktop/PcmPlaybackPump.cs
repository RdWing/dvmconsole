using System.Diagnostics;
using DvmConsole.Audio;

namespace DvmConsole.Desktop;

internal static class PcmPlaybackPump
{
    private const int InputBufferSamples = 1_600;

    public static async Task<bool> RunAsync(
        IAudioPcmStreamReader reader,
        IAudioPlayback playback,
        PcmRateConverter? rateConverter,
        CancellationToken cancellationToken,
        Func<ValueTask>? firstOutputWritten = null,
        IPcmPlaybackPacer? pacer = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(playback);
        pacer ??= new RealtimePcmPlaybackPacer(playback.Format);

        bool wroteOutput = false;
        short[] input = new short[InputBufferSamples];
        while (true)
        {
            int sampleCount = await reader.ReadSamplesAsync(input, cancellationToken)
                .ConfigureAwait(false);
            if (sampleCount == 0)
                return wroteOutput;

            ReadOnlyMemory<short> output = rateConverter is null
                ? input.AsMemory(0, sampleCount)
                : rateConverter.Convert(input.AsSpan(0, sampleCount));
            if (output.Length == 0)
                continue;

            await pacer.WaitBeforeWriteAsync(cancellationToken).ConfigureAwait(false);
            await playback.WriteAsync(output, cancellationToken).ConfigureAwait(false);
            pacer.ObserveWrittenSamples(output.Length);
            if (!wroteOutput)
            {
                wroteOutput = true;
                if (firstOutputWritten is not null)
                    await firstOutputWritten().ConfigureAwait(false);
            }
        }
    }
}

internal interface IPcmPlaybackPacer
{
    ValueTask WaitBeforeWriteAsync(CancellationToken cancellationToken);
    void ObserveWrittenSamples(int sampleCount);
}

// File and network decoders can produce PCM faster than real time. Pace their
// writes against a monotonic media clock so immediately accepting mixer lanes
// cannot be flooded and discard most of a recording before it is heard.
internal sealed class RealtimePcmPlaybackPacer : IPcmPlaybackPacer
{
    private readonly long startedAt = Stopwatch.GetTimestamp();
    private readonly int samplesPerSecond;
    private long writtenSamples;

    public RealtimePcmPlaybackPacer(PcmAudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        samplesPerSecond = checked(format.SampleRate * format.Channels);
    }

    public async ValueTask WaitBeforeWriteAsync(CancellationToken cancellationToken)
    {
        TimeSpan mediaElapsed = TimeSpan.FromSeconds(writtenSamples / (double)samplesPerSecond);
        TimeSpan actualElapsed = Stopwatch.GetElapsedTime(startedAt);
        TimeSpan remaining = mediaElapsed - actualElapsed;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
    }

    public void ObserveWrittenSamples(int sampleCount)
    {
        if (sampleCount < 0)
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        writtenSamples = checked(writtenSamples + sampleCount);
    }
}
