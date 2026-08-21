using DvmConsole.Audio;

namespace DvmConsole.Media;

internal static class ConcealmentAudioWriter
{
    public static async ValueTask WriteAsync(
        IAudioPlayback playback,
        ReadOnlyMemory<short> samples,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(playback);
        if (samples.IsEmpty)
            return;

        if (playback is IConcealmentAudioPlayback concealmentPlayback)
        {
            await concealmentPlayback.WriteConcealmentAsync(samples, cancellationToken)
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

    public static async ValueTask WriteAsync(
        IAudioPlayback playback,
        IReadOnlyList<short[]> frames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0)
            return;

        int sampleCount = 0;
        foreach (short[] frame in frames)
            sampleCount = checked(sampleCount + frame.Length);

        var samples = new short[sampleCount];
        int offset = 0;
        foreach (short[] frame in frames)
        {
            frame.CopyTo(samples, offset);
            offset += frame.Length;
        }

        await WriteAsync(playback, samples, cancellationToken).ConfigureAwait(false);
    }
}
