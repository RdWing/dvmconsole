// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;
using DvmConsole.Core.Runtime;
using DvmConsole.Vocoder;
using fnecore.P25;

namespace DvmConsole.Media;

// Decodes selected P25 DFSI LDUs to 8 kHz PCM. Encrypted IMBE codewords are
// decrypted only when the corresponding key resolves from the injected key
// ring; missing key material fails closed before audio is emitted.
public sealed class P25RxAudioSession : IAsyncDisposable
{
    private const int MaximumConcealedPackets = 10;

    private readonly P25TrafficSelector selector;
    private readonly VoiceFrameDecoder decoder;
    private readonly IAudioPlayback playback;
    private readonly IP25KeyResolver? keyResolver;
    private readonly string systemName;
    private readonly VoicePacketSequenceTracker sequenceTracker = new();
    private readonly byte[] imbe = new byte[P25DfsiFrameCodec.ImbeBytes];
    private readonly bool[] available = new bool[P25DfsiFrameCodec.CodewordsPerLdu];
    private readonly byte[] encryptionMessageIndicator = new byte[P25Defines.P25_MI_LENGTH];
    private readonly byte[] codeword = new byte[P25DfsiFrameCodec.CodewordBytes];
    private readonly short[] packetSamples = new short[
        P25DfsiFrameCodec.CodewordsPerLdu * VocoderFrameSizes.PcmSamplesPerFrame];
    private readonly short[] concealmentSamples = new short[
        MaximumConcealedPackets * P25DfsiFrameCodec.CodewordsPerLdu *
        VocoderFrameSizes.PcmSamplesPerFrame];
    private P25CryptoState? cryptoState;
    private uint activeStreamId;
    private bool encryptedStream;
    private bool disposed;

    public P25RxAudioSession(
        P25TrafficSelector selector,
        IVocoderSession vocoder,
        IAudioPlayback playback,
        IP25KeyResolver? keyResolver = null,
        string systemName = "")
    {
        this.selector = selector ?? throw new ArgumentNullException(nameof(selector));
        decoder = new VoiceFrameDecoder(vocoder, VocoderMode.P25Imbe);
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
        this.keyResolver = keyResolver;
        this.systemName = systemName ?? string.Empty;
    }

    public int FramesDecoded { get; private set; }
    public long MalformedPackets { get; private set; }
    public long LostPackets => sequenceTracker.LostPackets;
    public long DuplicateOrLatePackets => sequenceTracker.DuplicateOrLatePackets;

    public async ValueTask<int> ProcessAsync(IRadioMediaFrame traffic, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(traffic);
        if (!selector.Matches(traffic))
            return 0;
        if (activeStreamId != traffic.StreamId)
        {
            activeStreamId = traffic.StreamId;
            cryptoState = null;
            encryptedStream = false;
        }
        long lostBefore = sequenceTracker.LostPackets;
        if (!sequenceTracker.TryAccept(traffic.StreamId, traffic.PacketSequence))
            return 0;
        long lostPackets = sequenceTracker.LostPackets - lostBefore;
        if (lostPackets > 0)
        {
            await ConcealLostPacketsAsync(lostPackets, cancellationToken).ConfigureAwait(false);
            // An encrypted P25 keystream cannot be safely advanced across a
            // missing LDU. Wait for a fresh LDU1 rather than emitting
            // plausible-looking but incorrectly decrypted audio.
            MarkEncryptionDesynchronized();
        }

        if (!P25DfsiFrameCodec.TryParseVoiceLdu(
                traffic,
                imbe,
                available,
                encryptionMessageIndicator,
                out P25DfsiFrameCodec.P25ParsedLdu parsedLdu))
        {
            MalformedPackets++;
            MarkEncryptionDesynchronized();
            await ConcealCurrentLduAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        if (available.Contains(false))
            MalformedPackets++;

        bool ldu1 = parsedLdu.IsLdu1;
        bool hasEncryptionMetadata = parsedLdu.HasEncryptionMetadata;
        if (cryptoState is not null && cryptoState.StreamId != traffic.StreamId)
            cryptoState = null;

        if (ldu1)
        {
            PrepareForLdu1(traffic.StreamId, parsedLdu);
            if (encryptedStream && cryptoState is null)
            {
                // Sustained encrypted calls often omit HDU metadata after the
                // first LDU1. If loss destroyed the prepared stream, fail
                // closed until an LDU1 carries fresh HDU data or an LDU2 ESS
                // prepares the following LDU1.
                MalformedPackets++;
                await ConcealCurrentLduAsync(cancellationToken).ConfigureAwait(false);
                return 0;
            }
        }
        else if (hasEncryptionMetadata &&
                 parsedLdu.AlgorithmId == P25Defines.P25_ALGO_UNENCRYPT)
        {
            cryptoState = null;
            encryptedStream = false;
        }
        else if (cryptoState is null &&
                 hasEncryptionMetadata &&
                 parsedLdu.AlgorithmId != P25Defines.P25_ALGO_UNENCRYPT)
        {
            // The current LDU2 cannot be decrypted without the preceding
            // state, but its ESS describes the keystream for the following
            // LDU1. Prepare that next boundary, conceal this LDU, and recover
            // without ever sending ciphertext to the vocoder.
            encryptedStream = true;
            TryCreateCryptoState(
                traffic.StreamId,
                parsedLdu.AlgorithmId,
                parsedLdu.KeyId,
                encryptionMessageIndicator,
                out cryptoState);
            MalformedPackets++;
            await ConcealCurrentLduAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        else if (!ldu1 && encryptedStream && cryptoState is null)
        {
            MalformedPackets++;
            await ConcealCurrentLduAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        int errors = 0;
        for (int index = 0; index < P25DfsiFrameCodec.CodewordsPerLdu; index++)
        {
            P25DUID duid = ldu1 ? P25DUID.LDU1 : P25DUID.LDU2;
            Span<short> frameSamples = packetSamples.AsSpan(
                index * VocoderFrameSizes.PcmSamplesPerFrame,
                VocoderFrameSizes.PcmSamplesPerFrame);
            if (!available[index])
            {
                // Encryption is a continuous per-LDU keystream. Consume and
                // discard the missing slot's keystream so later valid records
                // stay aligned, then let the vocoder conceal exactly 20 ms.
                if (cryptoState is not null)
                {
                    Array.Clear(codeword);
                    ProcessEncryption(codeword, duid);
                }
                decoder.ProcessLost(frameSamples);
                FramesDecoded++;
                continue;
            }

            imbe.AsSpan(
                index * P25DfsiFrameCodec.CodewordBytes,
                P25DfsiFrameCodec.CodewordBytes).CopyTo(codeword);
            ProcessEncryption(codeword, duid);
            errors += decoder.Process(codeword, frameSamples);
            FramesDecoded++;
        }

        await WritePacketSegmentsAsync(packetSamples, available, cancellationToken)
            .ConfigureAwait(false);

        if (!ldu1 && cryptoState is not null)
            AdvanceAfterLdu2(traffic.StreamId, parsedLdu, errors);

        return errors;
    }

    private async ValueTask ConcealLostPacketsAsync(
        long lostPackets,
        CancellationToken cancellationToken)
    {
        int frameCount = checked((int)Math.Min(lostPackets, MaximumConcealedPackets)) *
            P25DfsiFrameCodec.CodewordsPerLdu;
        int sampleCount = checked(frameCount * VocoderFrameSizes.PcmSamplesPerFrame);
        Memory<short> concealedSamples = concealmentSamples.AsMemory(0, sampleCount);
        for (int index = 0; index < frameCount; index++)
        {
            decoder.ProcessLost(concealedSamples.Span.Slice(
                index * VocoderFrameSizes.PcmSamplesPerFrame,
                VocoderFrameSizes.PcmSamplesPerFrame));
            FramesDecoded++;
        }
        await ConcealmentAudioWriter.WriteAsync(playback, concealedSamples, cancellationToken)
            .ConfigureAwait(false);
        if (lostPackets > MaximumConcealedPackets)
            decoder.Reset();
    }

    private async ValueTask WritePacketSegmentsAsync(
        short[] packetSamples,
        bool[] available,
        CancellationToken cancellationToken)
    {
        int segmentStart = 0;
        while (segmentStart < available.Length)
        {
            bool concealment = !available[segmentStart];
            int segmentEnd = segmentStart + 1;
            while (segmentEnd < available.Length && (!available[segmentEnd]) == concealment)
                segmentEnd++;

            ReadOnlyMemory<short> segment = packetSamples.AsMemory(
                segmentStart * VocoderFrameSizes.PcmSamplesPerFrame,
                (segmentEnd - segmentStart) * VocoderFrameSizes.PcmSamplesPerFrame);
            if (concealment)
            {
                await ConcealmentAudioWriter.WriteAsync(playback, segment, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await LivePacketAudioWriter.WriteAsync(playback, segment, cancellationToken)
                    .ConfigureAwait(false);
            }
            segmentStart = segmentEnd;
        }
    }

    private ValueTask ConcealCurrentLduAsync(CancellationToken cancellationToken)
        => ConcealLostPacketsAsync(1, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        decoder.Dispose();
        await playback.DisposeAsync().ConfigureAwait(false);
        cryptoState = null;
        disposed = true;
    }

    private void PrepareForLdu1(
        uint streamId,
        P25DfsiFrameCodec.P25ParsedLdu parsedLdu)
    {
        // dvmhost emits HDU encryption metadata only at call start. Later
        // LDU1 DATA_UNIT frames rely on the next MI carried by the preceding
        // LDU2, so retain that prepared state when no fresh HDU is present.
        if (!parsedLdu.HasEncryptionMetadata)
            return;

        if (parsedLdu.AlgorithmId == P25Defines.P25_ALGO_UNENCRYPT)
        {
            cryptoState = null;
            encryptedStream = false;
            return;
        }

        cryptoState = CreateCryptoState(
            streamId,
            parsedLdu.AlgorithmId,
            parsedLdu.KeyId,
            encryptionMessageIndicator);
        encryptedStream = true;
    }

    private P25CryptoState CreateCryptoState(
        uint streamId,
        byte algorithmId,
        ushort keyId,
        ReadOnlySpan<byte> messageIndicator)
    {
        if (!TryCreateCryptoState(
                streamId,
                algorithmId,
                keyId,
                messageIndicator,
                out P25CryptoState? state))
        {
            throw new NotSupportedException(
                $"P25 encrypted receive requires key 0x{keyId:X} for algorithm 0x{algorithmId:X2}.");
        }

        return state!;
    }

    private bool TryCreateCryptoState(
        uint streamId,
        byte algorithmId,
        ushort keyId,
        ReadOnlySpan<byte> messageIndicator,
        out P25CryptoState? state)
    {
        state = null;
        if (keyResolver is null ||
            !keyResolver.TryResolve(systemName, algorithmId, keyId, out ReadOnlyMemory<byte> key))
        {
            return false;
        }

        var crypto = new P25Crypto();
        byte[] ownedKey = key.ToArray();
        byte[] ownedMessageIndicator = messageIndicator.ToArray();
        crypto.SetKey(keyId, algorithmId, ownedKey);
        if (!crypto.Prepare(algorithmId, keyId, ownedMessageIndicator))
        {
            throw new NotSupportedException(
                $"P25 algorithm 0x{algorithmId:X2} could not prepare the configured key stream.");
        }

        state = new P25CryptoState(
            streamId,
            algorithmId,
            keyId,
            ownedMessageIndicator,
            crypto);
        return true;
    }

    private void ProcessEncryption(byte[] codeword, P25DUID duid)
    {
        if (cryptoState is null)
            return;
        if (!cryptoState.Crypto.Process(codeword, duid))
            throw new NotSupportedException("P25 encrypted receive could not process the configured key stream.");
    }

    private void AdvanceAfterLdu2(
        uint streamId,
        P25DfsiFrameCodec.P25ParsedLdu parsedLdu,
        int errors)
    {
        if (parsedLdu.HasEncryptionMetadata)
        {
            if (parsedLdu.AlgorithmId == P25Defines.P25_ALGO_UNENCRYPT)
            {
                cryptoState = null;
                encryptedStream = false;
                return;
            }

            byte[] nextMessageIndicator = encryptionMessageIndicator.ToArray();
            if (errors > 0)
            {
                nextMessageIndicator = cryptoState!.MessageIndicator.ToArray();
                P25Crypto.CycleP25Lfsr(nextMessageIndicator);
            }

            P25Crypto nextCrypto = cryptoState!.Crypto;
            if (parsedLdu.AlgorithmId != cryptoState.AlgorithmId || parsedLdu.KeyId != cryptoState.KeyId)
            {
                if (keyResolver is null ||
                    !keyResolver.TryResolve(systemName, parsedLdu.AlgorithmId, parsedLdu.KeyId, out ReadOnlyMemory<byte> key))
                {
                    throw new NotSupportedException(
                        $"P25 encrypted receive requires key 0x{parsedLdu.KeyId:X} for algorithm 0x{parsedLdu.AlgorithmId:X2}.");
                }

                nextCrypto = new P25Crypto();
                nextCrypto.SetKey(parsedLdu.KeyId, parsedLdu.AlgorithmId, key.ToArray());
            }

            if (!nextCrypto.Prepare(parsedLdu.AlgorithmId, parsedLdu.KeyId, nextMessageIndicator))
            {
                throw new NotSupportedException(
                    $"P25 algorithm 0x{parsedLdu.AlgorithmId:X2} could not prepare the next key stream.");
            }

            cryptoState = new P25CryptoState(
                streamId,
                parsedLdu.AlgorithmId,
                parsedLdu.KeyId,
                nextMessageIndicator,
                nextCrypto);
            encryptedStream = true;
            return;
        }

        byte[] cycledMessageIndicator = cryptoState!.MessageIndicator.ToArray();
        // An encrypted LDU2 normally carries the next message indicator in
        // ESS. If those records are unavailable, advance the known indicator
        // with the standard P25 LFSR instead of reusing the just-consumed
        // keystream for the following LDU1.
        P25Crypto.CycleP25Lfsr(cycledMessageIndicator);
        cryptoState.Crypto.Prepare(
            cryptoState.AlgorithmId,
            cryptoState.KeyId,
            cycledMessageIndicator);
        cryptoState = cryptoState with { MessageIndicator = cycledMessageIndicator };
    }

    private void MarkEncryptionDesynchronized()
    {
        if (cryptoState is not null)
            encryptedStream = true;
        cryptoState = null;
    }

    private sealed record P25CryptoState(
        uint StreamId,
        byte AlgorithmId,
        ushort KeyId,
        byte[] MessageIndicator,
        P25Crypto Crypto);
}
