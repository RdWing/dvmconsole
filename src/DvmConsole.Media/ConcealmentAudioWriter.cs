using DvmConsole.Audio;

namespace DvmConsole.Media;

internal static class ConcealmentAudioWriter
{
    public static async ValueTask WriteAsync(
        IAudioPlayback playback,
        IReadOnlyList<short[]> frames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0)
            return;

        if (playback is not IConcealmentAudioPlayback concealmentPlayback)
        {
            foreach (short[] frame in frames)
                await playback.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            return;
        }

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

        await concealmentPlayback.WriteConcealmentAsync(samples, cancellationToken)
            .ConfigureAwait(false);
    }
}
