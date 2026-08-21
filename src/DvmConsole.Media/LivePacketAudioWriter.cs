using DvmConsole.Audio;

namespace DvmConsole.Media;

internal static class LivePacketAudioWriter
{
    public static async ValueTask WriteAsync(
        IAudioPlayback playback,
        ReadOnlyMemory<short> samples,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(playback);
        if (playback is ILivePacketAudioPlayback packetPlayback)
        {
            await packetPlayback.WriteLivePacketAsync(samples, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        int frameSamples = Math.Max(
            1,
            playback.Format.SampleRate * playback.Format.Channels / 50);
        for (int offset = 0; offset < samples.Length; offset += frameSamples)
        {
            int count = Math.Min(frameSamples, samples.Length - offset);
            await playback.WriteAsync(samples.Slice(offset, count), cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
