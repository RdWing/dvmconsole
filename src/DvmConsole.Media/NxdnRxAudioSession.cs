using DvmConsole.Audio;
using DvmConsole.FneClient;
using DvmConsole.Vocoder;

namespace DvmConsole.Media;

// Routes selected NXDN frames through an explicitly supplied NXDN decoder.
// No default backend is provided until the required FEC/AMBE+2 implementation
// is available, so normal application construction remains fail-closed.
public sealed class NxdnRxAudioSession : IAsyncDisposable
{
    private readonly NxdnTrafficSelector selector;
    private readonly INxdnVocoderSession vocoder;
    private readonly IAudioPlayback playback;
    private bool disposed;

    public NxdnRxAudioSession(
        NxdnTrafficSelector selector,
        INxdnVocoderSession vocoder,
        IAudioPlayback playback)
    {
        this.selector = selector ?? throw new ArgumentNullException(nameof(selector));
        this.vocoder = vocoder ?? throw new ArgumentNullException(nameof(vocoder));
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
    }

    public int FramesDecoded { get; private set; }
    public long MalformedPackets { get; private set; }

    public async ValueTask<int> ProcessAsync(
        FneTrafficFrame traffic,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(traffic);
        if (!selector.Matches(traffic))
            return 0;

        byte[] frame = new byte[NxdnVoicePacketCodec.FrameBytes];
        if (!NxdnVoicePacketCodec.TryExtractFrame(traffic.Payload, frame))
        {
            MalformedPackets++;
            return 0;
        }
        short[] samples = new short[VocoderFrameSizes.PcmSamplesPerFrame];
        int errors = vocoder.Decode(frame, samples);
        await playback.WriteAsync(samples, cancellationToken).ConfigureAwait(false);
        FramesDecoded++;
        return errors;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;

        vocoder.Dispose();
        await playback.DisposeAsync().ConfigureAwait(false);
        disposed = true;
    }
}
