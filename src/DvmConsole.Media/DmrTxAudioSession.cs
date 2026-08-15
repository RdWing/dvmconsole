using DvmConsole.Vocoder;
using fnecore.DMR;

namespace DvmConsole.Media;

/// <summary>
/// Aggregates three encoded DMR AMBE codewords into one FNE voice packet.
/// Call-control, link-control, terminators, and audio capture remain outside
/// this reusable media session.
/// </summary>
public sealed class DmrTxAudioSession : IDisposable
{
    private readonly uint sourceId;
    private readonly uint destinationId;
    private readonly byte slot;
    private readonly uint streamId;
    private readonly Action<ReadOnlyMemory<byte>, ushort, uint> send;
    private readonly VoiceFrameEncoder encoder;
    private readonly DmrTxPacketSequence sequence;
    private readonly EmbeddedData? embeddedData;
    private readonly List<byte> pendingAmbe = [];
    private byte embeddedSequence;
    private bool disposed;

    public DmrTxAudioSession(
        uint sourceId,
        uint destinationId,
        byte slot,
        uint streamId,
        IVocoderSession vocoder,
        Action<ReadOnlyMemory<byte>, ushort, uint> send,
        ushort packetSequence = 0,
        byte frameSequence = 0,
        byte embeddedSequence = 0,
        EmbeddedData? embeddedData = null)
        : this(
            sourceId,
            destinationId,
            slot,
            streamId,
            vocoder,
            send,
            new DmrTxPacketSequence(packetSequence, frameSequence),
            embeddedSequence,
            embeddedData)
    {
    }

    internal DmrTxAudioSession(
        uint sourceId,
        uint destinationId,
        byte slot,
        uint streamId,
        IVocoderSession vocoder,
        Action<ReadOnlyMemory<byte>, ushort, uint> send,
        DmrTxPacketSequence sequence,
        byte embeddedSequence,
        EmbeddedData? embeddedData)
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
        this.sequence = sequence ?? throw new ArgumentNullException(nameof(sequence));
        if (embeddedSequence > 5)
            throw new ArgumentOutOfRangeException(nameof(embeddedSequence));
        this.embeddedSequence = embeddedSequence;
        this.embeddedData = embeddedData;
        encoder = new VoiceFrameEncoder(vocoder ?? throw new ArgumentNullException(nameof(vocoder)), VocoderMode.DmrAmbe);
    }

    public int CodewordsEncoded { get; private set; }
    public int PacketsSent { get; private set; }

    /// <summary>
    /// Encodes complete 160-sample frames and emits a packet for every three
    /// codewords. Incomplete audio remains buffered until more samples arrive.
    /// </summary>
    public int Process(ReadOnlySpan<short> samples)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int packetsBefore = PacketsSent;
        encoder.Process(samples, EmitCodeword);
        return PacketsSent - packetsBefore;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        encoder.Dispose();
        pendingAmbe.Clear();
        disposed = true;
    }

    private void EmitCodeword(ReadOnlyMemory<byte> codeword)
    {
        pendingAmbe.AddRange(codeword.ToArray());
        CodewordsEncoded++;
        if (pendingAmbe.Count < DmrVoicePacketCodec.AmbeBytes)
            return;

        bool voiceSync = embeddedSequence == 0;
        byte[] packet = DmrVoicePacketCodec.CreateVoicePacket(
            sourceId,
            destinationId,
            slot,
            voiceSync,
            embeddedSequence,
            sequence.FrameSequence,
            pendingAmbe.ToArray(),
            embeddedData);
        ushort packetSequence = sequence.PacketSequence;
        send(packet, packetSequence, streamId);
        pendingAmbe.Clear();
        PacketsSent++;
        sequence.Advance();
        if (embeddedSequence >= 5)
            this.embeddedSequence = 0;
        else
            this.embeddedSequence++;
    }
}

/// <summary>
/// Owns the packet and DMR frame sequence numbers for one outbound call.
/// RTP sequence 65535 is reserved for call-end signaling and is never used
/// for a voice packet.
/// </summary>
public sealed class DmrTxPacketSequence
{
    public DmrTxPacketSequence(ushort packetSequence = 0, byte frameSequence = 0)
    {
        if (packetSequence == DmrVoicePacketCodec.RtpCallEndSequence)
            throw new ArgumentOutOfRangeException(nameof(packetSequence));

        PacketSequence = packetSequence;
        FrameSequence = frameSequence;
    }

    public ushort PacketSequence { get; private set; }
    public byte FrameSequence { get; private set; }

    public void Advance()
    {
        PacketSequence = PacketSequence >= DmrVoicePacketCodec.RtpCallEndSequence - 1
            ? (ushort)0
            : (ushort)(PacketSequence + 1);
        FrameSequence++;
    }
}
