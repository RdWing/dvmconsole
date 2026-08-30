namespace DvmConsole.Audio;

/// <summary>
/// Streaming decoder contract for sources that produce mono 16-bit PCM.
/// Implementations may wrap WAV, MP3, or another decoder without coupling the
/// desktop playback coordinator to a particular media library.
/// </summary>
/// <remarks>
/// A reader has one consumer: read operations must not overlap. Implementations
/// reject overlap with <see cref="InvalidOperationException"/>. Disposal stops
/// accepting operations, interrupts the underlying source where possible, and
/// does not finish until in-flight decoder work has released owned resources.
/// </remarks>
public interface IAudioPcmStreamReader : IAsyncDisposable
{
    int SampleRate { get; }
    ValueTask<int> ReadSamplesAsync(
        Memory<short> destination,
        CancellationToken cancellationToken = default);
}
