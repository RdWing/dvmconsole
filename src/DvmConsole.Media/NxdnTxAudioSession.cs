using DvmConsole.Vocoder;

namespace DvmConsole.Media;

// Aggregates four 20 ms AMBE+2 codewords into one 80 ms NXDN 4800 voice frame.
public sealed class NxdnTxAudioSession : IDisposable
{
    private readonly uint sourceId;
    private readonly uint destinationId;
    private readonly bool group;
    private readonly uint streamId;
    private readonly Action<ReadOnlyMemory<byte>, ushort, uint> send;
    private readonly VoiceFrameEncoder encoder;
    private readonly NxdnPrivacyProcessor? privacyProcessor;
    private readonly NxdnPrivacyOptions? privacy;
    private readonly NxdnVoiceSignalingCycle signalingCycle;
    private readonly byte[] pendingAmbe = new byte[NxdnVoicePacketCodec.AmbeBytes];
    private int pendingAmbeBytes;
    private int pendingPcmSamples;
    private ushort packetSequence;
    private byte frameSequence;
    private bool disposed;
    private List<NxdnOutboundPacket>? deferredPackets;

    public NxdnTxAudioSession(
        uint sourceId,
        uint destinationId,
        bool group,
        uint streamId,
        IVocoderSession vocoder,
        Action<ReadOnlyMemory<byte>, ushort, uint> send,
        ushort packetSequence = 0,
        byte frameSequence = 0,
        NxdnPrivacyOptions? privacy = null)
    {
        if (sourceId is 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(sourceId));
        if (destinationId is 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(destinationId));
        if (streamId == 0)
            throw new ArgumentOutOfRangeException(nameof(streamId));
        if (packetSequence == NxdnVoicePacketCodec.RtpCallEndSequence)
            throw new ArgumentOutOfRangeException(nameof(packetSequence));
        this.sourceId = sourceId;
        this.destinationId = destinationId;
        this.group = group;
        this.streamId = streamId;
        this.send = send ?? throw new ArgumentNullException(nameof(send));
        this.packetSequence = packetSequence;
        this.frameSequence = frameSequence;
        this.privacy = privacy;
        signalingCycle = new NxdnVoiceSignalingCycle(
            sourceId,
            destinationId,
            group,
            privacy?.AlgorithmId ?? 0,
            privacy?.KeyId ?? 0,
            privacy?.MessageIndicator ?? ReadOnlyMemory<byte>.Empty);
        if (privacy is not null)
        {
            if (vocoder is not IHalfRateVocoderSession halfRate)
                throw new NotSupportedException("NXDN privacy requires a vocoder with half-rate parameter access.");
            privacyProcessor = new NxdnPrivacyProcessor(halfRate, privacy);
        }
        encoder = new VoiceFrameEncoder(vocoder ?? throw new ArgumentNullException(nameof(vocoder)), VocoderMode.NxdnAmbe);
    }

    public int CodewordsEncoded { get; private set; }
    public int FramesSent { get; private set; }
    internal ushort PacketSequence => packetSequence;
    internal byte FrameSequence => frameSequence;

    public int Process(ReadOnlySpan<short> samples)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int before = FramesSent;
        pendingPcmSamples = (pendingPcmSamples + samples.Length) % VocoderFrameSizes.PcmSamplesPerFrame;
        encoder.Process(samples, EmitCodeword);
        return FramesSent - before;
    }

    internal int CompleteFrame()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int before = FramesSent;
        if (pendingPcmSamples > 0)
            Process(new short[VocoderFrameSizes.PcmSamplesPerFrame - pendingPcmSamples]);
        encoder.Flush(EmitCodeword);
        while (pendingAmbeBytes > 0)
            Process(new short[VocoderFrameSizes.PcmSamplesPerFrame]);
        return FramesSent - before;
    }

    internal IReadOnlyList<NxdnOutboundPacket> PrepareFrameCompletion()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (deferredPackets is not null)
            throw new InvalidOperationException("NXDN frame completion is already being prepared.");

        var packets = new List<NxdnOutboundPacket>();
        deferredPackets = packets;
        try
        {
            CompleteFrame();
            return packets;
        }
        finally
        {
            deferredPackets = null;
        }
    }

    internal void AdvanceSequence()
    {
        packetSequence = packetSequence >= NxdnVoicePacketCodec.RtpCallEndSequence - 1
            ? (ushort)0
            : (ushort)(packetSequence + 1);
        frameSequence++;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        encoder.Dispose();
        privacyProcessor?.Dispose();
        signalingCycle.Dispose();
        Array.Clear(pendingAmbe);
        pendingAmbeBytes = 0;
        disposed = true;
    }

    private void EmitCodeword(ReadOnlyMemory<byte> codeword)
    {
        Span<byte> destination = pendingAmbe.AsSpan(
            pendingAmbeBytes,
            NxdnVoicePacketCodec.CodewordBytes);
        if (privacyProcessor is not null)
            privacyProcessor.ProcessCodeword(codeword.Span, destination);
        else
            codeword.Span.CopyTo(destination);
        pendingAmbeBytes += destination.Length;
        CodewordsEncoded++;
        if (pendingAmbeBytes < pendingAmbe.Length)
            return;
        byte[] packet = NxdnVoicePacketCodec.CreateVoicePacket(
            sourceId,
            destinationId,
            group,
            frameSequence,
            pendingAmbe,
            superframePart: signalingCycle.SuperframePart,
            cipherType: privacy?.AlgorithmId ?? 0,
            keyId: privacy?.KeyId ?? 0,
            sacchMetadata: signalingCycle.CurrentMetadata);
        EmitPacket(packet, packetSequence);
        pendingAmbeBytes = 0;
        FramesSent++;
        AdvanceSequence();
        signalingCycle.AdvanceAfterVoiceFrame(privacyProcessor);
    }

    private void EmitPacket(byte[] packet, ushort sequence)
    {
        if (deferredPackets is not null)
            deferredPackets.Add(new NxdnOutboundPacket(packet, sequence, streamId));
        else
            send(packet, sequence, streamId);
    }
}

internal readonly record struct NxdnOutboundPacket(byte[] Payload, ushort Sequence, uint StreamId);
