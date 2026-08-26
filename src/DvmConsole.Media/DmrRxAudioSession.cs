using DvmConsole.Audio;
using DvmConsole.FneClient;
using DvmConsole.Vocoder;

namespace DvmConsole.Media;

// Decodes inbound DMR voice packets and writes their PCM frames to one audio
// playback device. Call/channel selection remains above this reusable session.
public sealed class DmrRxAudioSession : IAsyncDisposable
{
    private enum UnknownPrivacyResolution
    {
        AwaitingMetadata,
        Clear,
        Encrypted
    }

    private readonly IVocoderSession vocoder;
    private readonly VoiceFrameDecoder decoder;
    private readonly IAudioPlayback playback;
    private readonly IDmrKeyResolver? keyResolver;
    private readonly string systemName;
    private readonly bool configuredPrivacyRequired;
    private readonly bool privacyMayVary;
    private readonly IReceivePrivacyPolicy? privacyPolicy;
    private readonly DmrLateEntryMessageIndicator lateEntryCollector = new();
    private DmrPrivacyProcessor? privacyProcessor;
    private uint activeStreamId;
    private bool privacyRequired;
    private bool privacyStateKnown;
    private bool? streamIsEncrypted;
    private bool hasDecodedVoiceInActiveStream;
    private bool disposed;

    public DmrRxAudioSession(
        IVocoderSession vocoder,
        IAudioPlayback playback,
        IDmrKeyResolver? keyResolver = null,
        string systemName = "",
        bool privacyExpected = false,
        bool privacyMayVary = false,
        IReceivePrivacyPolicy? privacyPolicy = null)
    {
        this.vocoder = vocoder ?? throw new ArgumentNullException(nameof(vocoder));
        decoder = new VoiceFrameDecoder(vocoder, VocoderMode.DmrAmbe);
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
        this.keyResolver = keyResolver;
        this.systemName = systemName ?? string.Empty;
        configuredPrivacyRequired = privacyExpected;
        this.privacyMayVary = privacyMayVary;
        this.privacyPolicy = privacyPolicy;
        privacyRequired = privacyExpected;
        privacyStateKnown = !privacyMayVary;
    }

    public int FramesDecoded { get; private set; }
    public long MalformedPackets { get; private set; }

    public async ValueTask<int> ProcessAsync(FneTrafficFrame traffic, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(traffic);
        if (traffic.Protocol != FneTrafficProtocol.Dmr)
            return 0;

        if (activeStreamId != 0 && activeStreamId != traffic.StreamId)
        {
            ClearPrivacyState();
            hasDecodedVoiceInActiveStream = false;
        }
        activeStreamId = traffic.StreamId;

        if (DmrVoicePacketCodec.TryExtractVoiceEncryptionState(traffic.Payload, out bool encrypted))
        {
            streamIsEncrypted = encrypted;
            privacyStateKnown = true;
            privacyRequired = configuredPrivacyRequired || encrypted;
            if (!privacyRequired)
            {
                privacyProcessor?.Dispose();
                privacyProcessor = null;
            }
            return 0;
        }

        if (DmrVoicePacketCodec.IsPrivacyIndicator(traffic.Payload))
        {
            if (DmrVoicePacketCodec.TryExtractEncryptionMetadata(traffic.Payload, out var metadata))
            {
                streamIsEncrypted = true;
                PreparePrivacy(traffic.StreamId, metadata);
                privacyStateKnown = true;
            }
            else
                MalformedPackets++;
            return 0;
        }
        if (!traffic.FrameType.Equals("VOICE", StringComparison.OrdinalIgnoreCase) &&
            !traffic.FrameType.Equals("VOICE_SYNC", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (privacyPolicy?.RequireEncryptedTraffic == true && streamIsEncrypted == false)
            return 0;
        byte[] ambe = new byte[DmrVoicePacketCodec.AmbeBytes];
        if (!DmrVoicePacketCodec.TryExtractAmbe(traffic.Payload, ambe))
        {
            MalformedPackets++;
            privacyProcessor?.SkipCodewords(DmrVoicePacketCodec.CodewordsPerPacket);
            await ConcealCurrentPacketAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        byte voiceBurst = (byte)(traffic.Payload[15] & 0x0F);
        bool hasLateEntryMi = lateEntryCollector.AddVoiceBurst(
            voiceBurst,
            ambe,
            out byte[] lateEntryMi);
        DmrBurstFSignaling burstFSignaling = default;
        bool hasBurstFSignaling = voiceBurst == 5 &&
            DmrVoicePacketCodec.TryExtractBurstFSignaling(traffic.Payload, out burstFSignaling);

        if (!privacyStateKnown)
        {
            UnknownPrivacyResolution resolution = ResolveUnknownPrivacyState(
                traffic.StreamId,
                voiceBurst,
                hasLateEntryMi,
                lateEntryMi,
                hasBurstFSignaling,
                burstFSignaling);
            if (resolution != UnknownPrivacyResolution.Clear)
                return 0;
            if (privacyPolicy?.RequireEncryptedTraffic == true)
                return 0;
        }

        if (privacyRequired && privacyProcessor is null)
        {
            if (hasLateEntryMi && hasBurstFSignaling && TryPrepareLateEntry(
                traffic.StreamId,
                lateEntryMi,
                burstFSignaling))
            {
                return 0;
            }
            MalformedPackets++;
            await ConcealCurrentPacketAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        int errors = 0;
        short[] packetSamples = new short[
            DmrVoicePacketCodec.CodewordsPerPacket * VocoderFrameSizes.PcmSamplesPerFrame];
        Span<byte> parameters = stackalloc byte[VocoderFrameSizes.HalfRateParameterBytes];
        for (int index = 0; index < DmrVoicePacketCodec.CodewordsPerPacket; index++)
        {
            ReadOnlySpan<byte> codeword = ambe.AsSpan(
                index * DmrVoicePacketCodec.CodewordBytes,
                DmrVoicePacketCodec.CodewordBytes);
            Span<short> frameSamples = packetSamples.AsSpan(
                index * VocoderFrameSizes.PcmSamplesPerFrame,
                VocoderFrameSizes.PcmSamplesPerFrame);
            if (privacyProcessor is not null)
            {
                HalfRateFecStatus status = privacyProcessor.ExtractAndProcessParameters(
                    codeword,
                    parameters);
                ((IHalfRateVocoderSession)vocoder).DecodeParameters(
                    parameters,
                    frameSamples,
                    status.DecoderErrorMetric,
                    status.Unrecoverable);
                errors += checked((int)status.DecoderErrorMetric);
            }
            else
            {
                errors += decoder.Process(codeword, frameSamples);
            }
            FramesDecoded++;
            hasDecodedVoiceInActiveStream = true;
        }

        await LivePacketAudioWriter.WriteAsync(playback, packetSamples, cancellationToken)
            .ConfigureAwait(false);

        if (privacyProcessor is not null && hasLateEntryMi && hasBurstFSignaling)
            TryPrepareLateEntry(traffic.StreamId, lateEntryMi, burstFSignaling);

        return errors;
    }

    internal async ValueTask ConcealLostPacketsAsync(
        long lostPackets,
        CancellationToken cancellationToken = default)
    {
        if (!hasDecodedVoiceInActiveStream || lostPackets <= 0)
            return;

        const int maximumConcealedPackets = 10;
        int frameCount = checked((int)Math.Min(lostPackets, maximumConcealedPackets)) *
            DmrVoicePacketCodec.CodewordsPerPacket;
        var concealedSamples = new short[
            checked(frameCount * VocoderFrameSizes.PcmSamplesPerFrame)];
        for (int index = 0; index < frameCount; index++)
        {
            decoder.ProcessLost(concealedSamples.AsSpan(
                index * VocoderFrameSizes.PcmSamplesPerFrame,
                VocoderFrameSizes.PcmSamplesPerFrame));
            FramesDecoded++;
        }
        await ConcealmentAudioWriter.WriteAsync(playback, concealedSamples, cancellationToken)
            .ConfigureAwait(false);
        if (lostPackets > maximumConcealedPackets)
            decoder.Reset();
    }

    private ValueTask ConcealCurrentPacketAsync(CancellationToken cancellationToken)
        => ConcealLostPacketsAsync(1, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        privacyProcessor?.Dispose();
        decoder.Dispose();
        await playback.DisposeAsync().ConfigureAwait(false);
        disposed = true;
    }

    internal void InvalidateEncryption()
    {
        lateEntryCollector.Reset();
        if (!privacyRequired)
            return;
        privacyProcessor?.Dispose();
        privacyProcessor = null;
    }

    private void PreparePrivacy(uint streamId, DmrVoicePacketCodec.DmrEncryptionMetadata metadata)
    {
        if (metadata.AlgorithmId is not (DmrPrivacyAlgorithms.Arc4 or
            DmrPrivacyAlgorithms.DesOfb or DmrPrivacyAlgorithms.Aes256))
        {
            throw new NotSupportedException($"Unsupported DMR privacy algorithm 0x{metadata.AlgorithmId:X2}.");
        }
        if (keyResolver is null ||
            !keyResolver.TryResolve(systemName, metadata.AlgorithmId, metadata.KeyId, out ReadOnlyMemory<byte> key))
        {
            privacyRequired = true;
            throw new NotSupportedException(
                $"DMR encrypted receive requires key 0x{metadata.KeyId:X2} for algorithm 0x{metadata.AlgorithmId:X2}.");
        }
        if (vocoder is not IHalfRateVocoderSession halfRateVocoder)
            throw new NotSupportedException("DMR privacy requires a vocoder with half-rate parameter access.");

        privacyProcessor?.Dispose();
        privacyProcessor = new DmrPrivacyProcessor(
            halfRateVocoder,
            new DmrPrivacyOptions(
                metadata.AlgorithmId,
                metadata.KeyId,
                key,
                metadata.MessageIndicator));
        activeStreamId = streamId;
        privacyRequired = true;
        streamIsEncrypted = true;
    }

    private bool TryPrepareLateEntry(
        uint streamId,
        ReadOnlyMemory<byte> messageIndicator,
        DmrBurstFSignaling signaling)
    {
        if (!IsPrivacyAssociation(signaling))
            return false;

        PreparePrivacy(
            streamId,
            new DmrVoicePacketCodec.DmrEncryptionMetadata(
                signaling.AlgorithmId,
                signaling.KeyId,
                DmrPrivacyAlgorithms.FeatureId,
                0,
                true,
                messageIndicator.ToArray()));
        // Late-entry fragments advertise the MI that becomes active at the
        // next voice-sync burst, so the new processor is already aligned.
        return true;
    }

    private UnknownPrivacyResolution ResolveUnknownPrivacyState(
        uint streamId,
        byte voiceBurst,
        bool hasMessageIndicator,
        ReadOnlyMemory<byte> messageIndicator,
        bool hasBurstFSignaling,
        DmrBurstFSignaling signaling)
    {
        if (hasMessageIndicator && hasBurstFSignaling &&
            TryPrepareLateEntry(streamId, messageIndicator, signaling))
        {
            privacyStateKnown = true;
            privacyRequired = true;
            return UnknownPrivacyResolution.Encrypted;
        }

        if (voiceBurst != 5 ||
            (hasBurstFSignaling && IsPrivacyAssociation(signaling)))
        {
            return UnknownPrivacyResolution.AwaitingMetadata;
        }

        // A complete superframe without encryption identifiers is clear. Burst
        // F may legitimately carry reverse-channel or unrelated single-burst
        // signalling, so an all-zero payload is not required.
        privacyStateKnown = true;
        privacyRequired = false;
        streamIsEncrypted = false;
        return UnknownPrivacyResolution.Clear;
    }

    private static bool IsPrivacyAssociation(DmrBurstFSignaling signaling)
    {
        bool hasSupportedAlgorithm = signaling.AlgorithmId is
            DmrPrivacyAlgorithms.Arc4 or
            DmrPrivacyAlgorithms.DesOfb or
            DmrPrivacyAlgorithms.Aes256;
        return !signaling.IsReverseChannel &&
            signaling.Payload != 0 &&
            hasSupportedAlgorithm &&
            signaling.KeyId != 0;
    }

    private void ClearPrivacyState()
    {
        privacyProcessor?.Dispose();
        privacyProcessor = null;
        privacyRequired = configuredPrivacyRequired;
        privacyStateKnown = !privacyMayVary;
        lateEntryCollector.Reset();
        activeStreamId = 0;
        streamIsEncrypted = null;
    }
}
