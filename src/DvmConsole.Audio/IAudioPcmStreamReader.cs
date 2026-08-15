namespace DvmConsole.Audio;

/// <summary>
/// Streaming decoder contract for sources that produce mono 16-bit PCM.
/// Implementations may wrap WAV, MP3, or another decoder without coupling the
/// desktop playback coordinator to a particular media library.
/// </summary>
public interface IAudioPcmStreamReader : IAsyncDisposable
{
    int SampleRate { get; }
    ValueTask<int> ReadSamplesAsync(
        Memory<short> destination,
        CancellationToken cancellationToken = default);
}
