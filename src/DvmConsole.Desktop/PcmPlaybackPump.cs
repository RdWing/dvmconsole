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
        Func<ValueTask>? firstOutputWritten = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(playback);

        bool wroteOutput = false;
        short[] input = new short[InputBufferSamples];
        while (true)
        {
            int sampleCount = await reader.ReadSamplesAsync(input, cancellationToken)
                .ConfigureAwait(false);
            if (sampleCount == 0)
                return wroteOutput;

            short[] output = rateConverter?.Convert(input.AsSpan(0, sampleCount))
                ?? input.AsSpan(0, sampleCount).ToArray();
            if (output.Length == 0)
                continue;

            await playback.WriteAsync(output, cancellationToken).ConfigureAwait(false);
            if (!wroteOutput)
            {
                wroteOutput = true;
                if (firstOutputWritten is not null)
                    await firstOutputWritten().ConfigureAwait(false);
            }
        }
    }
}
