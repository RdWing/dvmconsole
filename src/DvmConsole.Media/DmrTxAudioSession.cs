using DvmConsole.Vocoder;
using fnecore.DMR;

namespace DvmConsole.Media;

// Aggregates three encoded DMR AMBE codewords into one FNE voice packet.
// Call-control, link-control, terminators, and audio capture remain outside
// this reusable media session.
public sealed class DmrTxAudioSession : IDisposable
{
    private const int CodewordsPerPacket = DmrVoicePacketCodec.CodewordsPerPacket;
    private readonly uint sourceId;
    private readonly uint destinationId;
    private readonly byte slot;
    private readonly uint streamId;
    private readonly Action<ReadOnlyMemory<byte>, ushort, uint> send;
    private readonly VoiceFrameEncoder encoder;
    private readonly DmrTxPacketSequence sequence;
    private readonly EmbeddedData? embeddedData;
    private readonly DmrPrivacyProcessor? privacyProcessor;
    private readonly DmrBurstFSignaling? encryptedBurstFSignaling;
    private readonly byte[] pendingAmbe = new byte[DmrVoicePacketCodec.AmbeBytes];
    private DmrLateEntryMessageIndicator? lateEntryMessageIndicator;
    private int pendingAmbeBytes;
    private byte embeddedSequence;
    private int pendingPcmSamples;
    private List<DmrOutboundPacket>? deferredPackets;
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
        EmbeddedData? embeddedData = null,
        DmrPrivacyOptions? privacy = null)
        : this(
            sourceId,
            destinationId,
            slot,
            streamId,
            vocoder,
            send,
            new DmrTxPacketSequence(packetSequence, frameSequence),
            embeddedSequence,
            embeddedData,
            privacy)
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
        EmbeddedData? embeddedData,
        DmrPrivacyOptions? privacy = null)
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
        if (privacy is not null)
        {
            if (vocoder is not IHalfRateVocoderSession halfRateVocoder)
            {
                throw new NotSupportedException(
                    "DMR privacy requires a vocoder with half-rate parameter access.");
            }
            privacyProcessor = new DmrPrivacyProcessor(halfRateVocoder, privacy);
            encryptedBurstFSignaling = DmrBurstFSignaling.EncryptionIdentifiers(
                privacy.AlgorithmId,
                privacy.KeyId);
        }
        encoder = new VoiceFrameEncoder(vocoder ?? throw new ArgumentNullException(nameof(vocoder)), VocoderMode.DmrAmbe);
    }

    public int CodewordsEncoded { get; private set; }
    public int PacketsSent { get; private set; }

    // Encodes complete 160-sample frames and emits a packet for every three
    // codewords. Incomplete audio remains buffered until more samples arrive.
    public int Process(ReadOnlySpan<short> samples)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int packetsBefore = PacketsSent;
        pendingPcmSamples = (pendingPcmSamples + samples.Length) % VocoderFrameSizes.PcmSamplesPerFrame;
        encoder.Process(samples, EmitCodeword);
        return PacketsSent - packetsBefore;
    }

    // Pads a released call with encoded silence so no partial PCM/AMBE packet
    // is discarded and the current six-burst DMR superframe is completed
    // before its terminator is emitted.
    internal int CompleteSuperframe()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int packetsBefore = PacketsSent;

        if (pendingPcmSamples > 0)
            Process(new short[VocoderFrameSizes.PcmSamplesPerFrame - pendingPcmSamples]);

        encoder.Flush(EmitCodeword);

        while (pendingAmbeBytes > 0)
            Process(new short[VocoderFrameSizes.PcmSamplesPerFrame]);

        while (embeddedSequence != 0)
            Process(new short[VocoderFrameSizes.PcmSamplesPerFrame * CodewordsPerPacket]);

        return PacketsSent - packetsBefore;
    }

    // Builds the remaining voice bursts without sending them. Call-level
    // orchestration can then preserve the 60 ms DMR burst cadence before the
    // terminator instead of overflowing a downstream modem queue.
    internal IReadOnlyList<DmrOutboundPacket> PrepareSuperframeCompletion()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (deferredPackets is not null)
            throw new InvalidOperationException("DMR superframe completion is already being prepared.");

        var packets = new List<DmrOutboundPacket>();
        deferredPackets = packets;
        try
        {
            CompleteSuperframe();
            return packets;
        }
        finally
        {
            deferredPackets = null;
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        privacyProcessor?.Dispose();
        encoder.Dispose();
        Array.Clear(pendingAmbe);
        pendingAmbeBytes = 0;
        pendingPcmSamples = 0;
        disposed = true;
    }

    private void EmitCodeword(ReadOnlyMemory<byte> codeword)
    {
        int codewordInBurst = pendingAmbeBytes / VocoderFrameSizes.HalfRateCodewordBytes;
        if (privacyProcessor is not null && embeddedSequence == 0 && codewordInBurst == 0)
        {
            // The late-entry fragments carried by this superframe announce
            // the MI that becomes active at the following voice-sync burst.
            lateEntryMessageIndicator = new DmrLateEntryMessageIndicator(
                privacyProcessor.GetNextMessageIndicator());
        }
        Span<byte> destination = pendingAmbe.AsSpan(
            pendingAmbeBytes,
            VocoderFrameSizes.HalfRateCodewordBytes);
        if (privacyProcessor is not null)
            privacyProcessor.ProcessCodeword(codeword.Span, destination);
        else
            codeword.Span.CopyTo(destination);
        lateEntryMessageIndicator?.ApplyFragment(destination, embeddedSequence, codewordInBurst);
        pendingAmbeBytes += destination.Length;
        CodewordsEncoded++;
        if (pendingAmbeBytes < pendingAmbe.Length)
            return;

        bool voiceSync = embeddedSequence == 0;
        byte[] packet = DmrVoicePacketCodec.CreateVoicePacket(
            sourceId,
            destinationId,
            slot,
            voiceSync,
            embeddedSequence,
            sequence.FrameSequence,
            pendingAmbe,
            embeddedData,
            embeddedSequence == 5 ? encryptedBurstFSignaling : null);
        ushort packetSequence = sequence.PacketSequence;
        EmitPacket(packet, packetSequence);
        pendingAmbeBytes = 0;
        PacketsSent++;
        sequence.Advance();
        if (embeddedSequence >= 5)
        {
            this.embeddedSequence = 0;
            lateEntryMessageIndicator = null;
        }
        else
            this.embeddedSequence++;

    }

    private void EmitPacket(byte[] packet, ushort packetSequence)
    {
        if (deferredPackets is not null)
            deferredPackets.Add(new DmrOutboundPacket(packet, packetSequence, streamId));
        else
            send(packet, packetSequence, streamId);
    }
}

internal readonly record struct DmrOutboundPacket(byte[] Payload, ushort Sequence, uint StreamId);

// Owns the packet and DMR frame sequence numbers for one outbound call.
// RTP sequence 65535 is reserved for call-end signaling and is never used
// for a voice packet.
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
