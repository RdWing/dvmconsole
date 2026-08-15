using fnecore.DMR;
using DvmConsole.Vocoder;

namespace DvmConsole.Media;

/// <summary>
/// Represents one explicit DMR group call. Start emits the voice LC header,
/// Process emits encoded voice packets, and End emits the LC terminator.
/// Microphone capture and FNE connection state remain owned by the host.
/// </summary>
public sealed class DmrTxCallSession : IDisposable
{
    private readonly uint sourceId;
    private readonly uint destinationId;
    private readonly byte slot;
    private readonly uint streamId;
    private readonly Action<ReadOnlyMemory<byte>, ushort, uint> send;
    private readonly DmrTxPacketSequence sequence;
    private readonly DmrTxAudioSession audio;
    private bool started;
    private bool ended;
    private bool disposed;

    public DmrTxCallSession(
        uint sourceId,
        uint destinationId,
        byte slot,
        uint streamId,
        IVocoderSession vocoder,
        Action<ReadOnlyMemory<byte>, ushort, uint> send,
        ushort packetSequence = 0,
        byte frameSequence = 0)
    {
        if (sourceId == 0 || sourceId > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(sourceId));
        if (destinationId == 0 || destinationId > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(destinationId));
        if (slot > 1)
            throw new ArgumentOutOfRangeException(nameof(slot));
        if (streamId == 0)
            throw new ArgumentOutOfRangeException(nameof(streamId));

        this.sourceId = sourceId;
        this.destinationId = destinationId;
        this.slot = slot;
        this.streamId = streamId;
        this.send = send ?? throw new ArgumentNullException(nameof(send));
        sequence = new DmrTxPacketSequence(packetSequence, frameSequence);

        var lc = new LC
        {
            FLCO = (byte)DMRFLCO.FLCO_GROUP,
            SrcId = sourceId,
            DstId = destinationId
        };
        var embedded = new EmbeddedData();
        embedded.SetLC(lc);
        audio = new DmrTxAudioSession(
            sourceId,
            destinationId,
            slot,
            streamId,
            vocoder ?? throw new ArgumentNullException(nameof(vocoder)),
            send,
            sequence,
            // A DMR voice LC header is followed by a voice-sync burst. The
            // legacy transmitter starts its N sequence at zero here; starting
            // at one produces an embedded-data burst before the FNE has seen
            // voice sync and causes some masters to drop the call.
            embeddedSequence: 0,
            embeddedData: embedded);
    }

    public bool IsStarted => started;
    public bool IsEnded => ended;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started)
            throw new InvalidOperationException("The DMR call has already started.");
        if (ended)
            throw new InvalidOperationException("The DMR call has already ended.");

        byte[] header = DmrVoicePacketCodec.CreateVoiceLcHeaderPacket(
            sourceId,
            destinationId,
            slot,
            sequence.FrameSequence);
        send(header, sequence.PacketSequence, streamId);
        sequence.Advance();
        started = true;
    }

    public int Process(ReadOnlySpan<short> samples)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!started || ended)
            throw new InvalidOperationException("The DMR call must be active before processing audio.");
        return audio.Process(samples);
    }

    public void End()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!started)
            throw new InvalidOperationException("The DMR call has not started.");
        if (ended)
            return;

        audio.CompleteSuperframe();
        byte[] terminator = DmrVoicePacketCodec.CreateTerminatorPacket(
            sourceId,
            destinationId,
            slot,
            sequence.FrameSequence);
        send(terminator, sequence.PacketSequence, streamId);
        sequence.Advance();
        ended = true;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        audio.Dispose();
        disposed = true;
    }
}
