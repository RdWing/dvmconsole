using DvmConsole.Audio;
using DvmConsole.FneClient;
using DvmConsole.Vocoder;

namespace DvmConsole.Media;

// Extracts the AMBE+2 codewords from selected 4800-baud NXDN frames and routes
// them through the mandatory software vocoder.
public sealed class NxdnRxAudioSession : IAsyncDisposable
{
    private readonly NxdnTrafficSelector selector;
    private readonly VoiceFrameDecoder decoder;
    private readonly IHalfRateVocoderSession? halfRateVocoder;
    private readonly IAudioPlayback playback;
    private readonly INxdnKeyResolver? keyResolver;
    private readonly string systemName;
    private readonly IReceivePrivacyPolicy? privacyPolicy;
    private readonly VoicePacketSequenceTracker sequenceTracker = new();
    private readonly NxdnSacchMessageCollector sacchCollector = new();
    private NxdnPrivacyProcessor? privacyProcessor;
    private byte privacyAlgorithm;
    private byte privacyKeyId;
    private byte[] privacyKey = [];
    private byte configuredPrivacyAlgorithm;
    private byte configuredPrivacyKeyId;
    private uint activeStreamId;
    private int lastVoiceCodewordCount;
    private bool hasDecodedVoiceInActiveStream;
    private bool? activeCallIsEncrypted;
    private bool disposed;

    public NxdnRxAudioSession(
        NxdnTrafficSelector selector,
        IVocoderSession vocoder,
        IAudioPlayback playback,
        INxdnKeyResolver? keyResolver = null,
        string? systemName = null,
        string? configuredAlgorithm = null,
        string? configuredKeyId = null,
        IReceivePrivacyPolicy? privacyPolicy = null)
    {
        this.selector = selector ?? throw new ArgumentNullException(nameof(selector));
        ArgumentNullException.ThrowIfNull(vocoder);
        halfRateVocoder = vocoder as IHalfRateVocoderSession;
        decoder = new VoiceFrameDecoder(vocoder, VocoderMode.NxdnAmbe);
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
        this.keyResolver = keyResolver;
        this.systemName = systemName ?? string.Empty;
        this.privacyPolicy = privacyPolicy;
        if (NxdnKeyRing.TryParseAlgorithmId(configuredAlgorithm, out byte algorithm) &&
            NxdnKeyRing.TryParseKeyId(configuredKeyId, out byte keyId))
        {
            configuredPrivacyAlgorithm = algorithm;
            configuredPrivacyKeyId = keyId;
            ConfigurePrivacy(algorithm, keyId);
        }
    }

    public int FramesDecoded { get; private set; }
    public long MalformedPackets { get; private set; }
    public long LostPackets => sequenceTracker.LostPackets;
    public long DuplicateOrLatePackets => sequenceTracker.DuplicateOrLatePackets;

    public async ValueTask<int> ProcessAsync(
        FneTrafficFrame traffic,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(traffic);
        if (!selector.Matches(traffic))
            return 0;
        if (activeStreamId != traffic.StreamId)
        {
            activeStreamId = traffic.StreamId;
            lastVoiceCodewordCount = 0;
            hasDecodedVoiceInActiveStream = false;
            activeCallIsEncrypted = null;
            sacchCollector.Reset();
            InvalidatePrivacyAfterLoss();
        }
        long lostBefore = sequenceTracker.LostPackets;
        if (!sequenceTracker.TryAccept(traffic.StreamId, traffic.PacketSequence))
            return 0;
        long lostPackets = sequenceTracker.LostPackets - lostBefore;
        if (lostPackets > 0)
        {
            await ConcealLostPacketsAsync(lostPackets, cancellationToken).ConfigureAwait(false);
            sacchCollector.Reset();
            InvalidatePrivacyAfterLoss();
        }

        if (NxdnVoicePacketCodec.TryExtractFacchCallMetadata(
            traffic.Payload,
            0,
            out NxdnVoicePacketCodec.CallMetadata firstMetadata))
        {
            HandleCallMetadata(firstMetadata);
            if (NxdnVoicePacketCodec.TryExtractFacchCallMetadata(
                traffic.Payload,
                1,
                out NxdnVoicePacketCodec.CallMetadata secondMetadata) &&
                !CallMetadataMatches(firstMetadata, secondMetadata))
            {
                HandleCallMetadata(secondMetadata);
            }
            return 0;
        }

        NxdnVoicePacketCodec.CallMetadata? metadataAfterVoice = null;
        if (sacchCollector.TryAccept(traffic.Payload, out NxdnVoicePacketCodec.CallMetadata sacchMetadata))
        {
            if (sacchMetadata.MessageType == NxdnVoicePacketCodec.VoiceCallIvMessageType)
                metadataAfterVoice = sacchMetadata;
            else
                HandleCallMetadata(sacchMetadata);
        }

        if (privacyPolicy?.RequireEncryptedTraffic == true && activeCallIsEncrypted != true)
            return 0;

        byte[] ambe = new byte[NxdnVoicePacketCodec.AmbeBytes];
        if (!NxdnVoicePacketCodec.TryExtractAmbe(traffic.Payload, ambe, out int codewordCount))
        {
            MalformedPackets++;
            await ConcealCurrentPacketAsync(cancellationToken).ConfigureAwait(false);
            sacchCollector.Reset();
            InvalidatePrivacyAfterLoss();
            return 0;
        }
        lastVoiceCodewordCount = codewordCount;
        int errors = 0;
        bool missingPrivacy = false;
        short[] packetSamples = new short[
            checked(codewordCount * VocoderFrameSizes.PcmSamplesPerFrame)];
        bool[] concealed = new bool[codewordCount];
        Span<byte> parameters = stackalloc byte[VocoderFrameSizes.HalfRateParameterBytes];
        for (int index = 0; index < codewordCount; index++)
        {
            ReadOnlySpan<byte> codeword = ambe.AsSpan(
                index * NxdnVoicePacketCodec.CodewordBytes,
                NxdnVoicePacketCodec.CodewordBytes);
            Span<short> frameSamples = packetSamples.AsSpan(
                index * VocoderFrameSizes.PcmSamplesPerFrame,
                VocoderFrameSizes.PcmSamplesPerFrame);
            if (privacyAlgorithm != 0)
            {
                if (privacyProcessor is null)
                {
                    missingPrivacy = true;
                    concealed[index] = true;
                    decoder.ProcessLost(frameSamples);
                    FramesDecoded++;
                    continue;
                }
                HalfRateFecStatus status = privacyProcessor.ExtractAndProcessParameters(
                    codeword,
                    parameters);
                halfRateVocoder!.DecodeParameters(
                    parameters,
                    frameSamples,
                    status.DecoderErrorMetric,
                    status.Unrecoverable);
                errors += checked((int)status.DecoderErrorMetric);
                FramesDecoded++;
                hasDecodedVoiceInActiveStream = true;
                continue;
            }
            errors += decoder.Process(codeword, frameSamples);
            FramesDecoded++;
            hasDecodedVoiceInActiveStream = true;
        }
        await WritePacketSegmentsAsync(packetSamples, concealed, cancellationToken)
            .ConfigureAwait(false);
        if (metadataAfterVoice is { } completedMetadata)
            HandleCallMetadata(completedMetadata);
        if (missingPrivacy)
            MalformedPackets++;
        return errors;
    }

    private async ValueTask ConcealLostPacketsAsync(
        long lostPackets,
        CancellationToken cancellationToken)
    {
        if (!hasDecodedVoiceInActiveStream || lostPackets <= 0 || lastVoiceCodewordCount <= 0)
            return;

        const int maximumConcealedPackets = 10;
        int frameCount = checked((int)Math.Min(lostPackets, maximumConcealedPackets)) *
            lastVoiceCodewordCount;
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

    private async ValueTask WritePacketSegmentsAsync(
        short[] packetSamples,
        bool[] concealed,
        CancellationToken cancellationToken)
    {
        int segmentStart = 0;
        while (segmentStart < concealed.Length)
        {
            bool isConcealed = concealed[segmentStart];
            int segmentEnd = segmentStart + 1;
            while (segmentEnd < concealed.Length && concealed[segmentEnd] == isConcealed)
                segmentEnd++;

            ReadOnlyMemory<short> segment = packetSamples.AsMemory(
                segmentStart * VocoderFrameSizes.PcmSamplesPerFrame,
                (segmentEnd - segmentStart) * VocoderFrameSizes.PcmSamplesPerFrame);
            if (isConcealed)
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

    private void InvalidatePrivacyAfterLoss()
    {
        if (privacyAlgorithm == 0)
            return;
        privacyProcessor?.Dispose();
        privacyProcessor = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;

        privacyProcessor?.Dispose();
        if (privacyKey.Length > 0)
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(privacyKey);
        decoder.Dispose();
        await playback.DisposeAsync().ConfigureAwait(false);
        disposed = true;
    }

    private void HandleCallMetadata(NxdnVoicePacketCodec.CallMetadata metadata)
    {
        if (metadata.MessageType == NxdnVoicePacketCodec.TransmitReleaseMessageType)
        {
            activeCallIsEncrypted = null;
            ClearPrivacy();
            return;
        }
        if (metadata.MessageType == NxdnVoicePacketCodec.VoiceCallMessageType)
        {
            activeCallIsEncrypted = metadata.CipherType != 0;
            if (metadata.CipherType == 0)
            {
                ClearPrivacy(restoreConfigured: false);
                RestoreConfiguredPrivacy();
                return;
            }
            if (privacyAlgorithm == metadata.CipherType &&
                privacyKeyId == metadata.KeyId &&
                privacyKey.Length > 0 &&
                (privacyProcessor is not null || RequiresMessageIndicator(metadata.CipherType)))
            {
                return;
            }
            ConfigurePrivacy(metadata.CipherType, metadata.KeyId);
            return;
        }
        if (metadata.MessageType == NxdnVoicePacketCodec.VoiceCallIvMessageType &&
            privacyAlgorithm is NxdnPrivacyAlgorithms.Des or NxdnPrivacyAlgorithms.Aes256 &&
            privacyKey.Length > 0 && halfRateVocoder is not null)
        {
            privacyProcessor?.Dispose();
            privacyProcessor = new NxdnPrivacyProcessor(
                halfRateVocoder,
                new NxdnPrivacyOptions(privacyAlgorithm, privacyKeyId, privacyKey, metadata.MessageIndicator));
        }
    }

    private static bool RequiresMessageIndicator(byte algorithm)
        => algorithm is NxdnPrivacyAlgorithms.Des or NxdnPrivacyAlgorithms.Aes256;

    private void ClearPrivacy(bool restoreConfigured = true)
    {
        privacyProcessor?.Dispose();
        privacyProcessor = null;
        privacyAlgorithm = 0;
        privacyKeyId = 0;
        if (privacyKey.Length > 0)
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(privacyKey);
        privacyKey = [];
        if (restoreConfigured)
            RestoreConfiguredPrivacy();
    }

    private void ConfigurePrivacy(byte algorithm, byte keyId)
    {
        privacyProcessor?.Dispose();
        privacyProcessor = null;
        if (privacyKey.Length > 0)
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(privacyKey);
        privacyKey = [];
        privacyAlgorithm = algorithm;
        privacyKeyId = keyId;
        if (halfRateVocoder is null || keyResolver is null ||
            !keyResolver.TryResolve(systemName, privacyAlgorithm, privacyKeyId, out ReadOnlyMemory<byte> resolved))
        {
            return;
        }
        privacyKey = resolved.ToArray();
        if (privacyAlgorithm == NxdnPrivacyAlgorithms.Ehr)
        {
            privacyProcessor = new NxdnPrivacyProcessor(
                halfRateVocoder,
                new NxdnPrivacyOptions(privacyAlgorithm, privacyKeyId, privacyKey));
        }
    }

    private void RestoreConfiguredPrivacy()
    {
        if (configuredPrivacyAlgorithm != 0 && configuredPrivacyKeyId != 0)
            ConfigurePrivacy(configuredPrivacyAlgorithm, configuredPrivacyKeyId);
    }

    private static bool CallMetadataMatches(
        NxdnVoicePacketCodec.CallMetadata first,
        NxdnVoicePacketCodec.CallMetadata second)
        => first.MessageType == second.MessageType &&
            first.SourceId == second.SourceId &&
            first.DestinationId == second.DestinationId &&
            first.Group == second.Group &&
            first.CipherType == second.CipherType &&
            first.KeyId == second.KeyId &&
            first.MessageIndicator.AsSpan().SequenceEqual(second.MessageIndicator);
}
