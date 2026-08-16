using System.Security.Cryptography;
using DvmConsole.Vocoder;
using fnecore.P25;

namespace DvmConsole.Media;

/// <summary>
/// Key-stream inputs for one encrypted P25 transmit call. The message
/// indicator is copied so the call owns an immutable starting point.
/// </summary>
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

/// <summary>
/// Aggregates nine IMBE codewords into alternating clear P25 LDU1/LDU2
/// payloads. When encryption options are supplied, IMBE codewords are
/// encrypted through the same P25Crypto key-stream boundary used by receive;
/// HDU and LDU2 encryption-sync metadata are emitted with the voice payloads.
/// </summary>
public sealed class P25TxAudioSession : IDisposable
{
    private readonly uint sourceId;
    private readonly uint destinationId;
    private readonly uint streamId;
    private readonly Action<ReadOnlyMemory<byte>, ushort, uint> send;
    private readonly VoiceFrameEncoder encoder;
    private readonly P25TxEncryptionOptions? encryption;
    private readonly P25Crypto? crypto;
    private readonly byte[] messageIndicator;
    private readonly List<byte> pendingImbe = [];
    private int pendingPcmSamples;
    private ushort packetSequence;
    private bool sendLdu1 = true;
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
        encoder = new VoiceFrameEncoder(vocoder ?? throw new ArgumentNullException(nameof(vocoder)), VocoderMode.P25Imbe);
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

    /// <summary>
    /// Pads the final PCM frame and LDU with encoded silence so releasing PTT
    /// does not discard the tail of a call.
    /// </summary>
    internal int CompleteLdu()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int packetsBefore = LdusSent;

        if (pendingPcmSamples > 0)
            Process(new short[VocoderFrameSizes.PcmSamplesPerFrame - pendingPcmSamples]);

        while (pendingImbe.Count > 0)
            Process(new short[VocoderFrameSizes.PcmSamplesPerFrame]);

        return LdusSent - packetsBefore;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        encoder.Dispose();
        pendingImbe.Clear();
        pendingPcmSamples = 0;
        disposed = true;
    }

    private void EmitCodeword(ReadOnlyMemory<byte> codeword)
    {
        byte[] wireCodeword = codeword.ToArray();
        if (crypto is not null &&
            !crypto.Process(wireCodeword, sendLdu1 ? P25DUID.LDU1 : P25DUID.LDU2))
        {
            throw new InvalidOperationException("P25 encryption could not process the transmit codeword.");
        }

        pendingImbe.AddRange(wireCodeword);
        CodewordsEncoded++;
        if (pendingImbe.Count < P25DfsiFrameCodec.ImbeBytes)
            return;

        byte[] payload;
        if (encryption is null)
        {
            payload = sendLdu1
                ? P25DfsiFrameCodec.CreateLdu1Payload(sourceId, destinationId, pendingImbe.ToArray())
                : P25DfsiFrameCodec.CreateLdu2Payload(sourceId, destinationId, pendingImbe.ToArray());
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
                    pendingImbe.ToArray(),
                    metadata)
                : P25DfsiFrameCodec.CreateEncryptedLdu2Payload(
                    sourceId,
                    destinationId,
                    pendingImbe.ToArray(),
                    metadata);
        }

        send(payload, packetSequence, streamId);
        pendingImbe.Clear();
        LdusSent++;
        packetSequence = packetSequence >= P25DfsiFrameCodec.RtpCallEndSequence - 1
            ? (ushort)0
            : (ushort)(packetSequence + 1);
        sendLdu1 = !sendLdu1;
    }
}
