using DvmConsole.Audio;

namespace DvmConsole.Media;

// Forwards PCM to a shared output without taking ownership of that output.
// Receive sessions can therefore be added or removed independently while the
// coordinator owns the single native playback device.
public sealed class SharedAudioPlayback : IAudioPlayback, IAudioPlaybackContinuityDiagnostics
{
    private readonly IAudioPlayback inner;

    public SharedAudioPlayback(IAudioPlayback inner)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public PcmAudioFormat Format => inner.Format;
    public int? QueuedSamples => inner.QueuedSamples;
    public TimeSpan StarvedDuration =>
        (inner as IAudioPlaybackContinuityDiagnostics)?.StarvedDuration ?? TimeSpan.Zero;

    public void EndExpectedPlayback()
    {
        if (inner is IAudioPlaybackContinuityDiagnostics diagnostics)
            diagnostics.EndExpectedPlayback();
    }

    public ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
        => inner.WriteAsync(samples, cancellationToken);

    public ValueTask FlushAsync(CancellationToken cancellationToken = default)
        => inner.FlushAsync(cancellationToken);

    public ValueTask<int?> DrainAsync(CancellationToken cancellationToken = default)
        => inner.DrainAsync(cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
