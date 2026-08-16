using DvmConsole.Audio;
using DvmConsole.FneClient;

namespace DvmConsole.Media;

// Routes selected FNE analog PCM frames directly to the shared playback path.
public sealed class AnalogRxAudioSession : IAsyncDisposable
{
    private readonly AnalogTrafficSelector selector;
    private readonly IAudioPlayback playback;
    private bool disposed;

    public AnalogRxAudioSession(AnalogTrafficSelector selector, IAudioPlayback playback)
    {
        this.selector = selector ?? throw new ArgumentNullException(nameof(selector));
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
    }

    public int FramesDecoded { get; private set; }
    public long MalformedPackets { get; private set; }

    public async ValueTask<int> ProcessAsync(FneTrafficFrame traffic, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(traffic);
        if (!selector.Matches(traffic))
            return 0;

        short[] samples = new short[AnalogVoicePacketCodec.SamplesPerPacket];
        if (!AnalogVoicePacketCodec.TryExtractPcm(traffic.Payload, samples))
        {
            MalformedPackets++;
            return 0;
        }

        await playback.WriteAsync(samples, cancellationToken).ConfigureAwait(false);
        FramesDecoded++;
        return 0;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        await playback.DisposeAsync().ConfigureAwait(false);
        disposed = true;
    }
}
