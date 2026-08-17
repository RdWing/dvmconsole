using DvmConsole.Vocoder;
using System.Security.Cryptography;

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
    private readonly List<byte> pendingAmbe = [];
    private int pendingPcmSamples;
    private ushort packetSequence;
    private byte frameSequence;
    private bool disposed;
    private bool privacyIvPending;

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
        while (pendingAmbe.Count > 0)
            Process(new short[VocoderFrameSizes.PcmSamplesPerFrame]);
        return FramesSent - before;
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
        pendingAmbe.Clear();
        disposed = true;
    }

    private void EmitCodeword(ReadOnlyMemory<byte> codeword)
    {
        if (privacyIvPending)
            SendNextPrivacyIv();
        byte[] wireCodeword = codeword.ToArray();
        if (privacyProcessor is not null)
        {
            byte[] encrypted = new byte[NxdnVoicePacketCodec.CodewordBytes];
            privacyProcessor.ProcessCodeword(wireCodeword, encrypted);
            wireCodeword = encrypted;
        }
        pendingAmbe.AddRange(wireCodeword);
        CodewordsEncoded++;
        if (pendingAmbe.Count < NxdnVoicePacketCodec.AmbeBytes)
            return;
        byte[] packet = NxdnVoicePacketCodec.CreateVoicePacket(
            sourceId,
            destinationId,
            group,
            frameSequence,
            pendingAmbe.ToArray(),
            superframePart: (byte)(FramesSent % 4),
            cipherType: privacy?.AlgorithmId ?? 0,
            keyId: privacy?.KeyId ?? 0);
        send(packet, packetSequence, streamId);
        pendingAmbe.Clear();
        FramesSent++;
        AdvanceSequence();
        if (privacy is not null &&
            privacy.AlgorithmId is NxdnPrivacyAlgorithms.Des or NxdnPrivacyAlgorithms.Aes256 &&
            CodewordsEncoded % 32 == 0)
        {
            privacyIvPending = true;
        }
    }

    private void SendNextPrivacyIv()
    {
        byte[] messageIndicator = RandomNumberGenerator.GetBytes(NxdnPrivacyAlgorithms.MessageIndicatorBytes);
        byte[] packet = NxdnVoicePacketCodec.CreateCallControlPacket(
            sourceId,
            destinationId,
            group,
            NxdnVoicePacketCodec.VoiceCallIvMessageType,
            frameSequence,
            messageIndicator: messageIndicator);
        send(packet, packetSequence, streamId);
        AdvanceSequence();
        privacyProcessor!.ResetMessageIndicator(messageIndicator);
        CryptographicOperations.ZeroMemory(messageIndicator);
        privacyIvPending = false;
    }
}
