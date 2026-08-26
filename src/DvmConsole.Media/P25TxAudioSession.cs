using System.Security.Cryptography;
using DvmConsole.Vocoder;
using fnecore.P25;

namespace DvmConsole.Media;

// Key-stream inputs for one encrypted P25 transmit call. The message
// indicator is copied so the call owns an immutable starting point.
public sealed class P25TxEncryptionOptions
{
    public P25TxEncryptionOptions(
        byte algorithmId,
        ushort keyId,
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> messageIndicator)
    {
        if (algorithmId is not (P25Defines.P25_ALGO_DES or P25Defines.P25_ALGO_AES or P25Defines.P25_ALGO_ARC4))
            throw new ArgumentOutOfRangeException(nameof(algorithmId));
        if (keyId == 0)
            throw new ArgumentOutOfRangeException(nameof(keyId));
        if (key.Length == 0)
            throw new ArgumentException("P25 encryption key material is required.", nameof(key));
        if (messageIndicator.Length < P25Defines.P25_MI_LENGTH)
            throw new ArgumentException("P25 encryption requires a 9-byte message indicator.", nameof(messageIndicator));

        AlgorithmId = algorithmId;
        KeyId = keyId;
        Key = key.ToArray();
        MessageIndicator = messageIndicator[..P25Defines.P25_MI_LENGTH].ToArray();
    }

    public byte AlgorithmId { get; }
    public ushort KeyId { get; }
    public ReadOnlyMemory<byte> Key { get; }
    public ReadOnlyMemory<byte> MessageIndicator { get; }

    public static P25TxEncryptionOptions CreateRandom(
        byte algorithmId,
        ushort keyId,
        ReadOnlyMemory<byte> key)
    {
        byte[] messageIndicator = new byte[P25Defines.P25_MI_LENGTH];
        RandomNumberGenerator.Fill(messageIndicator);
        return new P25TxEncryptionOptions(algorithmId, keyId, key, messageIndicator);
    }
}

// Aggregates nine IMBE codewords into alternating clear P25 LDU1/LDU2
// payloads. When encryption options are supplied, IMBE codewords are
// encrypted through the same P25Crypto key-stream boundary used by receive;
// HDU and LDU2 encryption-sync metadata are emitted with the voice payloads.
public sealed class P25TxAudioSession : IDisposable
{
    private readonly uint sourceId;
    private readonly uint destinationId;
    private readonly uint streamId;
    private readonly Action<ReadOnlyMemory<byte>, ushort, uint> send;
    private readonly VoiceFrameEncoder encoder;
    private readonly IP25GeneratedToneVocoderSession? generatedToneVocoder;
    private readonly P25TxEncryptionOptions? encryption;
    private readonly P25Crypto? crypto;
    private readonly byte[] messageIndicator;
    private readonly byte[] pendingImbe = new byte[P25DfsiFrameCodec.ImbeBytes];
    private readonly byte[] wireCodeword = new byte[P25DfsiFrameCodec.CodewordBytes];
    private int pendingImbeBytes;
    private int pendingPcmSamples;
    private ushort packetSequence;
    private bool sendLdu1 = true;
    private List<P25OutboundPacket>? deferredPackets;
    private bool disposed;

    public P25TxAudioSession(
        uint sourceId,
        uint destinationId,
        uint streamId,
        IVocoderSession vocoder,
        Action<ReadOnlyMemory<byte>, ushort, uint> send,
        ushort packetSequence = 0,
        P25TxEncryptionOptions? encryption = null)
    {
        if (sourceId == 0 || sourceId > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(sourceId));
        if (destinationId == 0 || destinationId > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(destinationId));
        if (streamId == 0)
            throw new ArgumentOutOfRangeException(nameof(streamId));
        if (packetSequence == P25DfsiFrameCodec.RtpCallEndSequence)
            throw new ArgumentOutOfRangeException(nameof(packetSequence));

        this.sourceId = sourceId;
        this.destinationId = destinationId;
        this.streamId = streamId;
        this.send = send ?? throw new ArgumentNullException(nameof(send));
        this.packetSequence = packetSequence;
        this.encryption = encryption;
        messageIndicator = encryption?.MessageIndicator.ToArray() ?? [];
        if (encryption is not null)
        {
            crypto = new P25Crypto();
            crypto.SetKey(encryption.KeyId, encryption.AlgorithmId, encryption.Key.ToArray());
            if (!crypto.Prepare(encryption.AlgorithmId, encryption.KeyId, messageIndicator))
            {
                throw new InvalidOperationException(
                    $"P25 algorithm 0x{encryption.AlgorithmId:X2} could not prepare the transmit key stream.");
            }
        }
        ArgumentNullException.ThrowIfNull(vocoder);
        generatedToneVocoder = vocoder as IP25GeneratedToneVocoderSession;
        encoder = new VoiceFrameEncoder(vocoder, VocoderMode.P25Imbe);
    }

    public int CodewordsEncoded { get; private set; }
    public int LdusSent { get; private set; }

    public int Process(ReadOnlySpan<short> samples)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int packetsBefore = LdusSent;
        pendingPcmSamples = (pendingPcmSamples + samples.Length) % VocoderFrameSizes.PcmSamplesPerFrame;
        encoder.Process(samples, EmitCodeword);
        return LdusSent - packetsBefore;
    }

    public int ProcessSingleTone(double frequencyHz)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        EnsureFrameAlignedGeneratedAudio();
        byte[] codeword = new byte[P25DfsiFrameCodec.CodewordBytes];
        IP25GeneratedToneVocoderSession toneVocoder = generatedToneVocoder ??
            throw new NotSupportedException("The active P25 vocoder does not provide generated-tone lookup frames.");
        toneVocoder.EncodeSingleTone(frequencyHz, codeword);
        int packetsBefore = LdusSent;
        EmitCodeword(codeword);
        return LdusSent - packetsBefore;
    }

    // Pads the final PCM frame and LDU with encoded silence so releasing PTT
    // does not discard the tail of a call.
    private int CompleteLdu()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int packetsBefore = LdusSent;

        if (pendingPcmSamples > 0)
            Process(new short[VocoderFrameSizes.PcmSamplesPerFrame - pendingPcmSamples]);

        encoder.Flush(EmitCodeword);

        while (pendingImbeBytes > 0)
            Process(new short[VocoderFrameSizes.PcmSamplesPerFrame]);

        return LdusSent - packetsBefore;
    }

    internal IReadOnlyList<P25OutboundPacket> PrepareLduCompletion()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (deferredPackets is not null)
            throw new InvalidOperationException("P25 LDU completion is already being prepared.");

        var packets = new List<P25OutboundPacket>();
        deferredPackets = packets;
        try
        {
            CompleteLdu();
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
        encoder.Dispose();
        Array.Clear(pendingImbe);
        Array.Clear(wireCodeword);
        pendingImbeBytes = 0;
        pendingPcmSamples = 0;
        disposed = true;
    }

    private void EnsureFrameAlignedGeneratedAudio()
    {
        if (pendingPcmSamples != 0)
            throw new InvalidOperationException("Generated P25 lookup frames require frame-aligned PCM boundaries.");
    }

    private void EmitCodeword(ReadOnlyMemory<byte> codeword)
    {
        codeword.Span.CopyTo(wireCodeword);
        if (crypto is not null &&
            !crypto.Process(wireCodeword, sendLdu1 ? P25DUID.LDU1 : P25DUID.LDU2))
        {
            throw new InvalidOperationException("P25 encryption could not process the transmit codeword.");
        }

        wireCodeword.CopyTo(pendingImbe, pendingImbeBytes);
        pendingImbeBytes += wireCodeword.Length;
        CodewordsEncoded++;
        if (pendingImbeBytes < pendingImbe.Length)
            return;

        byte[] payload;
        if (encryption is null)
        {
            payload = sendLdu1
                ? P25DfsiFrameCodec.CreateLdu1Payload(sourceId, destinationId, pendingImbe)
                : P25DfsiFrameCodec.CreateLdu2Payload(sourceId, destinationId, pendingImbe);
        }
        else
        {
            if (!sendLdu1)
            {
                P25Crypto.CycleP25Lfsr(messageIndicator);
                if (!crypto!.Prepare(encryption.AlgorithmId, encryption.KeyId, messageIndicator))
                {
                    throw new InvalidOperationException(
                        $"P25 algorithm 0x{encryption.AlgorithmId:X2} could not prepare the next transmit key stream.");
                }
            }

            var metadata = new P25DfsiFrameCodec.P25EncryptionMetadata(
                encryption.AlgorithmId,
                encryption.KeyId,
                messageIndicator.ToArray());
            payload = sendLdu1
                ? P25DfsiFrameCodec.CreateEncryptedLdu1Payload(
                    sourceId,
                    destinationId,
                    pendingImbe,
                    metadata)
                : P25DfsiFrameCodec.CreateEncryptedLdu2Payload(
                    sourceId,
                    destinationId,
                    pendingImbe,
                    metadata);
        }

        EmitPacket(payload, packetSequence);
        pendingImbeBytes = 0;
        LdusSent++;
        packetSequence = packetSequence >= P25DfsiFrameCodec.RtpCallEndSequence - 1
            ? (ushort)0
            : (ushort)(packetSequence + 1);
        sendLdu1 = !sendLdu1;
    }

    private void EmitPacket(byte[] payload, ushort sequence)
    {
        if (deferredPackets is not null)
            deferredPackets.Add(new P25OutboundPacket(payload, sequence, streamId));
        else
            send(payload, sequence, streamId);
    }
}

internal readonly record struct P25OutboundPacket(byte[] Payload, ushort Sequence, uint StreamId);
