using System.Diagnostics;

namespace DvmConsole.Audio;

internal interface IPcmWriteTarget
{
    int Write(short[] samples, int count);
}

internal static class PcmWriteProgressWatchdog
{
    public static async ValueTask WriteAllAsync(
        IPcmWriteTarget target,
        short[] ownedBuffer,
        int sampleCount,
        TimeSpan noProgressTimeout,
        string noProgressMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(ownedBuffer);
        ArgumentException.ThrowIfNullOrWhiteSpace(noProgressMessage);
        if (sampleCount < 0 || sampleCount > ownedBuffer.Length)
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        if (noProgressTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(noProgressTimeout));

        int remaining = sampleCount;
        long lastProgress = Stopwatch.GetTimestamp();
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int written = target.Write(ownedBuffer, remaining);
            if (written < 0)
                MacCoreAudioBackend.EnsureSuccess(written, "write CoreAudio playback");
            if (written > remaining)
            {
                throw new IOException(
                    $"CoreAudio reported {written} written samples for a {remaining}-sample buffer.");
            }

            if (written > 0)
            {
                remaining -= written;
                if (remaining > 0)
                    Array.Copy(ownedBuffer, written, ownedBuffer, 0, remaining);
                lastProgress = Stopwatch.GetTimestamp();
                continue;
            }

            if (Stopwatch.GetElapsedTime(lastProgress) >= noProgressTimeout)
                throw new IOException(noProgressMessage);
            await Task.Delay(2, cancellationToken).ConfigureAwait(false);
        }
    }
}
