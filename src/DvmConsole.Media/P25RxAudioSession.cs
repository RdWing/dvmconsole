using DvmConsole.Audio;
using DvmConsole.FneClient;
using DvmConsole.Vocoder;

namespace DvmConsole.Media;

/// <summary>
/// Decodes selected, clear P25 DFSI LDUs to 8 kHz PCM. Decryption and key
/// management are intentionally not hidden in this class; encrypted traffic
/// requires a future protocol/security layer before it can be decoded safely.
/// </summary>
public sealed class P25RxAudioSession : IAsyncDisposable
{
    private readonly P25TrafficSelector selector;
    private readonly VoiceFrameDecoder decoder;
    private readonly IAudioPlayback playback;
    private bool disposed;

    public P25RxAudioSession(P25TrafficSelector selector, IVocoderSession vocoder, IAudioPlayback playback)
    {
        this.selector = selector ?? throw new ArgumentNullException(nameof(selector));
        decoder = new VoiceFrameDecoder(vocoder, VocoderMode.P25Imbe);
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
    }

    public int FramesDecoded { get; private set; }

    public async ValueTask<int> ProcessAsync(FneTrafficFrame traffic, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(traffic);
        if (!selector.Matches(traffic))
            return 0;

        byte[] imbe = P25DfsiFrameCodec.ExtractImbe(traffic);
        int errors = 0;
        for (int index = 0; index < P25DfsiFrameCodec.CodewordsPerLdu; index++)
        {
            short[] samples = [];
            errors += decoder.Process(
                imbe.AsSpan(index * P25DfsiFrameCodec.CodewordBytes, P25DfsiFrameCodec.CodewordBytes),
                decoded => samples = decoded.ToArray());
            await playback.WriteAsync(samples, cancellationToken).ConfigureAwait(false);
            FramesDecoded++;
        }

        return errors;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        decoder.Dispose();
        await playback.DisposeAsync().ConfigureAwait(false);
        disposed = true;
    }
}
