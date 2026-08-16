using DvmConsole.Core.Runtime;
using DvmConsole.Vocoder;

namespace DvmConsole.Media;

// Adapts an already-decoded PCM stream to one patch target. It reuses the
// normal outbound call lifecycles and deliberately fails closed for targets
// whose encryption or vocoder boundary is not available.
public sealed class PatchTransmitSession : IDisposable
{
    private readonly ChannelRuntimeDefinition target;
    private readonly DmrTxCallSession? dmr;
    private readonly P25TxCallSession? p25;
    private readonly AnalogTxAudioSession? analog;
    private bool started;
    private bool ended;
    private bool disposed;

    public PatchTransmitSession(
        ChannelRuntimeDefinition target,
        uint sourceId,
        uint streamId,
        IVocoderSession? vocoder,
        Action<ReadOnlyMemory<byte>, ushort, uint> send,
        P25TxEncryptionOptions? encryption = null)
    {
        this.target = target ?? throw new ArgumentNullException(nameof(target));
        ArgumentNullException.ThrowIfNull(send);
        if (target.RxOnly)
            throw new InvalidOperationException("An RX-only channel cannot be a patch target.");
        if (target.IsEncrypted && target.Mode != "p25")
            throw new NotSupportedException("Encrypted non-P25 patch targets are not supported.");
        if (target.Mode == "p25" && target.IsEncrypted && encryption is null)
            throw new InvalidOperationException("An encrypted P25 patch target requires a resolved key.");
        if (target.Mode != "p25" && encryption is not null)
            throw new ArgumentException("P25 encryption options are valid only for P25 targets.", nameof(encryption));

        switch (target.Mode)
        {
            case "dmr":
                ArgumentNullException.ThrowIfNull(vocoder);
                dmr = new DmrTxCallSession(sourceId, target.DestinationId, target.Slot, streamId, vocoder, send);
                break;
            case "p25":
                ArgumentNullException.ThrowIfNull(vocoder);
                p25 = new P25TxCallSession(sourceId, target.DestinationId, streamId, vocoder, send, encryption);
                break;
            case "analog":
                analog = new AnalogTxAudioSession(sourceId, target.DestinationId, streamId, send);
                break;
            case "nxdn":
                throw new NotSupportedException("NXDN patch targets are not implemented yet.");
            default:
                throw new ArgumentException($"Unsupported patch target mode '{target.Mode}'.", nameof(target));
        }
    }

    public ChannelRuntimeDefinition Target => target;
    public bool IsStarted => started;
    public bool IsEnded => ended;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started)
            throw new InvalidOperationException("The patch call has already started.");
        if (ended)
            throw new InvalidOperationException("The patch call has already ended.");

        dmr?.Start();
        p25?.Start();
        analog?.Start();
        started = true;
    }

    public int Process(ReadOnlySpan<short> samples)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!started || ended)
            throw new InvalidOperationException("The patch call must be active before processing audio.");

        if (dmr is not null)
            return dmr.Process(samples);
        if (p25 is not null)
            return p25.Process(samples);
        return analog!.Process(samples);
    }

    public void End()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!started)
            throw new InvalidOperationException("The patch call has not started.");
        if (ended)
            return;

        dmr?.End();
        p25?.End();
        analog?.End();
        ended = true;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        dmr?.Dispose();
        p25?.Dispose();
        analog?.Dispose();
        disposed = true;
    }
}
