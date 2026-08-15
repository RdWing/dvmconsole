using DvmConsole.Audio;
using DvmConsole.FneClient;
using DvmConsole.Vocoder;

namespace DvmConsole.Media;

/// <summary>
/// Decodes inbound DMR voice packets and writes their PCM frames to one audio
/// playback device. Call/channel selection remains above this reusable session.
/// </summary>
public sealed class DmrRxAudioSession : IAsyncDisposable
{
    private readonly VoiceFrameDecoder decoder;
    private readonly IAudioPlayback playback;
    private bool disposed;

    public DmrRxAudioSession(IVocoderSession vocoder, IAudioPlayback playback)
    {
        decoder = new VoiceFrameDecoder(vocoder, VocoderMode.DmrAmbe);
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
    }

    public int FramesDecoded { get; private set; }

    public async ValueTask<int> ProcessAsync(FneTrafficFrame traffic, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(traffic);
        if (traffic.Protocol != FneTrafficProtocol.Dmr)
            return 0;
        if (!traffic.FrameType.Equals("VOICE", StringComparison.OrdinalIgnoreCase) &&
            !traffic.FrameType.Equals("VOICE_SYNC", StringComparison.OrdinalIgnoreCase))
            return 0;

        byte[] ambe = new byte[DmrVoicePacketCodec.AmbeBytes];
        if (!DmrVoicePacketCodec.TryExtractAmbe(traffic.Payload, ambe))
            return 0;
        int errors = 0;
        for (int index = 0; index < DmrVoicePacketCodec.CodewordsPerPacket; index++)
        {
            short[] samples = [];
            errors += decoder.Process(
                ambe.AsSpan(index * DmrVoicePacketCodec.CodewordBytes, DmrVoicePacketCodec.CodewordBytes),
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
