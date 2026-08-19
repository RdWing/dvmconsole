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
    private readonly VoicePacketSequenceTracker sequenceTracker = new();
    private NxdnPrivacyProcessor? privacyProcessor;
    private byte privacyAlgorithm;
    private byte privacyKeyId;
    private byte[] privacyKey = [];
    private byte configuredPrivacyAlgorithm;
    private byte configuredPrivacyKeyId;
    private uint activeStreamId;
    private int lastVoiceCodewordCount;
    private bool hasDecodedVoiceInActiveStream;
    private bool disposed;

    public NxdnRxAudioSession(
        NxdnTrafficSelector selector,
        IVocoderSession vocoder,
        IAudioPlayback playback,
        INxdnKeyResolver? keyResolver = null,
        string? systemName = null,
        string? configuredAlgorithm = null,
        string? configuredKeyId = null)
    {
        this.selector = selector ?? throw new ArgumentNullException(nameof(selector));
        ArgumentNullException.ThrowIfNull(vocoder);
        halfRateVocoder = vocoder as IHalfRateVocoderSession;
        decoder = new VoiceFrameDecoder(vocoder, VocoderMode.NxdnAmbe);
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
        this.keyResolver = keyResolver;
        this.systemName = systemName ?? string.Empty;
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
        }
        long lostBefore = sequenceTracker.LostPackets;
        if (!sequenceTracker.TryAccept(traffic.StreamId, traffic.PacketSequence))
            return 0;
        long lostPackets = sequenceTracker.LostPackets - lostBefore;
        if (lostPackets > 0)
        {
            await ConcealLostPacketsAsync(lostPackets, cancellationToken).ConfigureAwait(false);
            InvalidatePrivacyAfterLoss();
        }

        if (NxdnVoicePacketCodec.TryExtractCallMetadata(traffic.Payload, out var metadata))
        {
            HandleCallMetadata(metadata);
            return 0;
        }

        byte[] ambe = new byte[NxdnVoicePacketCodec.AmbeBytes];
        if (!NxdnVoicePacketCodec.TryExtractAmbe(traffic.Payload, ambe, out int codewordCount))
        {
            MalformedPackets++;
            await ConcealCurrentPacketAsync(cancellationToken).ConfigureAwait(false);
            InvalidatePrivacyAfterLoss();
            return 0;
        }
        lastVoiceCodewordCount = codewordCount;
        int errors = 0;
        bool missingPrivacy = false;
        for (int index = 0; index < codewordCount; index++)
        {
            ReadOnlySpan<byte> codeword = ambe.AsSpan(
                index * NxdnVoicePacketCodec.CodewordBytes,
                NxdnVoicePacketCodec.CodewordBytes);
            if (privacyAlgorithm != 0)
            {
                if (privacyProcessor is null)
                {
                    missingPrivacy = true;
                    await ConcealFrameAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
                byte[] parameters = new byte[VocoderFrameSizes.HalfRateParameterBytes];
                HalfRateFecStatus status = privacyProcessor.ExtractAndProcessParameters(
                    codeword,
                    parameters);
                short[] decryptedSamples = new short[VocoderFrameSizes.PcmSamplesPerFrame];
                halfRateVocoder!.DecodeParameters(
                    parameters,
                    decryptedSamples,
                    status.DecoderErrorMetric,
                    status.Unrecoverable);
                errors += checked((int)status.DecoderErrorMetric);
                await playback.WriteAsync(decryptedSamples, cancellationToken).ConfigureAwait(false);
                FramesDecoded++;
                hasDecodedVoiceInActiveStream = true;
                continue;
            }
            short[]? samples = null;
            errors += decoder.Process(
                codeword,
                decoded => samples = decoded.ToArray());
            await playback.WriteAsync(samples!, cancellationToken).ConfigureAwait(false);
            FramesDecoded++;
            hasDecodedVoiceInActiveStream = true;
        }
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
        for (int index = 0; index < frameCount; index++)
            await ConcealFrameAsync(cancellationToken).ConfigureAwait(false);
        if (lostPackets > maximumConcealedPackets)
            decoder.Reset();
    }

    private ValueTask ConcealCurrentPacketAsync(CancellationToken cancellationToken)
        => ConcealLostPacketsAsync(1, cancellationToken);

    private async ValueTask ConcealFrameAsync(CancellationToken cancellationToken)
    {
        short[] samples = [];
        decoder.ProcessLost(decoded => samples = decoded.ToArray());
        await playback.WriteAsync(samples, cancellationToken).ConfigureAwait(false);
        FramesDecoded++;
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
            ClearPrivacy();
            return;
        }
        if (metadata.MessageType == NxdnVoicePacketCodec.VoiceCallMessageType)
        {
            ClearPrivacy(restoreConfigured: false);
            if (metadata.CipherType == 0)
            {
                RestoreConfiguredPrivacy();
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
}
