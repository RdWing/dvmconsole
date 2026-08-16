using DvmConsole.Audio;
using DvmConsole.FneClient;
using DvmConsole.Vocoder;
using fnecore.P25;

namespace DvmConsole.Media;

// Decodes selected P25 DFSI LDUs to 8 kHz PCM. Encrypted IMBE codewords are
// decrypted only when the corresponding key resolves from the injected key
// ring; missing key material fails closed before audio is emitted.
public sealed class P25RxAudioSession : IAsyncDisposable
{
    private readonly P25TrafficSelector selector;
    private readonly VoiceFrameDecoder decoder;
    private readonly IAudioPlayback playback;
    private readonly IP25KeyResolver? keyResolver;
    private readonly VoicePacketSequenceTracker sequenceTracker = new();
    private P25CryptoState? cryptoState;
    private bool disposed;

    public P25RxAudioSession(
        P25TrafficSelector selector,
        IVocoderSession vocoder,
        IAudioPlayback playback,
        IP25KeyResolver? keyResolver = null)
    {
        this.selector = selector ?? throw new ArgumentNullException(nameof(selector));
        decoder = new VoiceFrameDecoder(vocoder, VocoderMode.P25Imbe);
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
        this.keyResolver = keyResolver;
    }

    public int FramesDecoded { get; private set; }
    public long MalformedPackets { get; private set; }
    public long LostPackets => sequenceTracker.LostPackets;
    public long DuplicateOrLatePackets => sequenceTracker.DuplicateOrLatePackets;

    public async ValueTask<int> ProcessAsync(FneTrafficFrame traffic, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(traffic);
        if (!selector.Matches(traffic))
            return 0;
        long lostBefore = sequenceTracker.LostPackets;
        if (!sequenceTracker.TryAccept(traffic.StreamId, traffic.PacketSequence))
            return 0;
        if (sequenceTracker.LostPackets > lostBefore)
        {
            // An encrypted P25 keystream cannot be safely advanced across a
            // missing LDU. Wait for a fresh LDU1 rather than emitting
            // plausible-looking but incorrectly decrypted audio.
            cryptoState = null;
        }

        byte[] imbe = new byte[P25DfsiFrameCodec.ImbeBytes];
        if (!P25DfsiFrameCodec.TryExtractImbe(traffic, imbe))
        {
            MalformedPackets++;
            return 0;
        }

        bool ldu1 = traffic.Subtype.Equals("LDU1", StringComparison.OrdinalIgnoreCase);
        bool hasEncryptionMetadata = P25DfsiFrameCodec.TryExtractEncryptionMetadata(
            traffic,
            out P25DfsiFrameCodec.P25EncryptionMetadata encryptionMetadata);
        if (cryptoState is not null && cryptoState.StreamId != traffic.StreamId)
            cryptoState = null;

        if (ldu1)
            PrepareForLdu1(traffic);
        else if (cryptoState is null &&
                 hasEncryptionMetadata &&
                 encryptionMetadata.AlgorithmId != P25Defines.P25_ALGO_UNENCRYPT)
        {
            // A lost or malformed LDU1 makes the encrypted LDU2 unsafe to
            // decode. Drop only this frame and wait for the next LDU1 so a
            // sustained call can recover without tearing down the audio
            // session or emitting plausible-looking garbage.
            MalformedPackets++;
            return 0;
        }

        int errors = 0;
        for (int index = 0; index < P25DfsiFrameCodec.CodewordsPerLdu; index++)
        {
            byte[] codeword = imbe.AsSpan(
                index * P25DfsiFrameCodec.CodewordBytes,
                P25DfsiFrameCodec.CodewordBytes).ToArray();
            ProcessEncryption(codeword, ldu1 ? P25DUID.LDU1 : P25DUID.LDU2);
            short[] samples = [];
            errors += decoder.Process(
                codeword,
                decoded => samples = decoded.ToArray());
            await playback.WriteAsync(samples, cancellationToken).ConfigureAwait(false);
            FramesDecoded++;
        }

        if (!ldu1 && cryptoState is not null)
            AdvanceAfterLdu2(traffic, errors);

        return errors;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        decoder.Dispose();
        await playback.DisposeAsync().ConfigureAwait(false);
        cryptoState = null;
        disposed = true;
    }

    private void PrepareForLdu1(FneTrafficFrame traffic)
    {
        cryptoState = null;
        if (!P25DfsiFrameCodec.TryExtractEncryptionMetadata(traffic, out P25DfsiFrameCodec.P25EncryptionMetadata metadata) ||
            metadata.AlgorithmId == P25Defines.P25_ALGO_UNENCRYPT)
        {
            return;
        }

        cryptoState = CreateCryptoState(traffic.StreamId, metadata);
    }

    private P25CryptoState CreateCryptoState(uint streamId, P25DfsiFrameCodec.P25EncryptionMetadata metadata)
    {
        if (keyResolver is null ||
            !keyResolver.TryResolve(metadata.AlgorithmId, metadata.KeyId, out ReadOnlyMemory<byte> key))
        {
            throw new NotSupportedException(
                $"P25 encrypted receive requires key 0x{metadata.KeyId:X} for algorithm 0x{metadata.AlgorithmId:X2}.");
        }

        var crypto = new P25Crypto();
        crypto.SetKey(metadata.KeyId, metadata.AlgorithmId, key.ToArray());
        if (!crypto.Prepare(metadata.AlgorithmId, metadata.KeyId, metadata.MessageIndicator))
        {
            throw new NotSupportedException(
                $"P25 algorithm 0x{metadata.AlgorithmId:X2} could not prepare the configured key stream.");
        }

        return new P25CryptoState(
            streamId,
            metadata.AlgorithmId,
            metadata.KeyId,
            metadata.MessageIndicator.ToArray(),
            crypto);
    }

    private void ProcessEncryption(byte[] codeword, P25DUID duid)
    {
        if (cryptoState is null)
            return;
        if (!cryptoState.Crypto.Process(codeword, duid))
            throw new NotSupportedException("P25 encrypted receive could not process the configured key stream.");
    }

    private void AdvanceAfterLdu2(FneTrafficFrame traffic, int errors)
    {
        if (P25DfsiFrameCodec.TryExtractEncryptionMetadata(
                traffic,
                out P25DfsiFrameCodec.P25EncryptionMetadata metadata))
        {
            if (metadata.AlgorithmId == P25Defines.P25_ALGO_UNENCRYPT)
            {
                cryptoState = null;
                return;
            }

            byte[] nextMessageIndicator = metadata.MessageIndicator.ToArray();
            if (errors > 0)
            {
                nextMessageIndicator = cryptoState!.MessageIndicator.ToArray();
                P25Crypto.CycleP25Lfsr(nextMessageIndicator);
            }

            P25Crypto nextCrypto = cryptoState!.Crypto;
            if (metadata.AlgorithmId != cryptoState.AlgorithmId || metadata.KeyId != cryptoState.KeyId)
            {
                if (keyResolver is null ||
                    !keyResolver.TryResolve(metadata.AlgorithmId, metadata.KeyId, out ReadOnlyMemory<byte> key))
                {
                    throw new NotSupportedException(
                        $"P25 encrypted receive requires key 0x{metadata.KeyId:X} for algorithm 0x{metadata.AlgorithmId:X2}.");
                }

                nextCrypto = new P25Crypto();
                nextCrypto.SetKey(metadata.KeyId, metadata.AlgorithmId, key.ToArray());
            }

            if (!nextCrypto.Prepare(metadata.AlgorithmId, metadata.KeyId, nextMessageIndicator))
            {
                throw new NotSupportedException(
                    $"P25 algorithm 0x{metadata.AlgorithmId:X2} could not prepare the next key stream.");
            }

            cryptoState = new P25CryptoState(
                traffic.StreamId,
                metadata.AlgorithmId,
                metadata.KeyId,
                nextMessageIndicator,
                nextCrypto);
            return;
        }

        byte[] cycledMessageIndicator = cryptoState!.MessageIndicator.ToArray();
        if (errors > 0)
            P25Crypto.CycleP25Lfsr(cycledMessageIndicator);
        cryptoState.Crypto.Prepare(
            cryptoState.AlgorithmId,
            cryptoState.KeyId,
            cycledMessageIndicator);
        cryptoState = cryptoState with { MessageIndicator = cycledMessageIndicator };
    }

    private sealed record P25CryptoState(
        uint StreamId,
        byte AlgorithmId,
        ushort KeyId,
        byte[] MessageIndicator,
        P25Crypto Crypto);
}
