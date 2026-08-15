using DvmConsole.Audio;

namespace DvmConsole.Media;

/// <summary>
/// Forwards PCM to a shared output without taking ownership of that output.
/// Receive sessions can therefore be added or removed independently while the
/// coordinator owns the single native playback device.
/// </summary>
public sealed class SharedAudioPlayback : IAudioPlayback
{
    private readonly IAudioPlayback inner;

    public SharedAudioPlayback(IAudioPlayback inner)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public PcmAudioFormat Format => inner.Format;

    public ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
        => inner.WriteAsync(samples, cancellationToken);

    public ValueTask FlushAsync(CancellationToken cancellationToken = default)
        => inner.FlushAsync(cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
