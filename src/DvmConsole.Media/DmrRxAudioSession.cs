using DvmConsole.Audio;
using DvmConsole.FneClient;
using DvmConsole.Vocoder;

namespace DvmConsole.Media;

// Decodes inbound DMR voice packets and writes their PCM frames to one audio
// playback device. Call/channel selection remains above this reusable session.
public sealed class DmrRxAudioSession : IAsyncDisposable
{
    private readonly IVocoderSession vocoder;
    private readonly VoiceFrameDecoder decoder;
    private readonly IAudioPlayback playback;
    private readonly IDmrKeyResolver? keyResolver;
    private readonly string systemName;
    private readonly bool configuredPrivacyRequired;
    private DmrPrivacyProcessor? privacyProcessor;
    private uint activeStreamId;
    private bool privacyRequired;
    private bool hasDecodedVoiceInActiveStream;
    private bool disposed;

    public DmrRxAudioSession(
        IVocoderSession vocoder,
        IAudioPlayback playback,
        IDmrKeyResolver? keyResolver = null,
        string systemName = "",
        bool privacyExpected = false)
    {
        this.vocoder = vocoder ?? throw new ArgumentNullException(nameof(vocoder));
        decoder = new VoiceFrameDecoder(vocoder, VocoderMode.DmrAmbe);
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
        this.keyResolver = keyResolver;
        this.systemName = systemName ?? string.Empty;
        configuredPrivacyRequired = privacyExpected;
        privacyRequired = privacyExpected;
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

        if (DmrVoicePacketCodec.IsPrivacyIndicator(traffic.Payload))
        {
            if (DmrVoicePacketCodec.TryExtractEncryptionMetadata(traffic.Payload, out var metadata))
                PreparePrivacy(traffic.StreamId, metadata);
            else
                MalformedPackets++;
            return 0;
        }
        if (!traffic.FrameType.Equals("VOICE", StringComparison.OrdinalIgnoreCase) &&
            !traffic.FrameType.Equals("VOICE_SYNC", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (privacyRequired && privacyProcessor is null)
        {
            MalformedPackets++;
            await ConcealCurrentPacketAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        byte[] ambe = new byte[DmrVoicePacketCodec.AmbeBytes];
        if (!DmrVoicePacketCodec.TryExtractAmbe(traffic.Payload, ambe))
        {
            MalformedPackets++;
            privacyProcessor?.SkipCodewords(DmrVoicePacketCodec.CodewordsPerPacket);
            await ConcealCurrentPacketAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        int errors = 0;
        for (int index = 0; index < DmrVoicePacketCodec.CodewordsPerPacket; index++)
        {
            byte[] codeword = ambe.AsSpan(
                index * DmrVoicePacketCodec.CodewordBytes,
                DmrVoicePacketCodec.CodewordBytes).ToArray();
            if (privacyProcessor is not null)
            {
                byte[] parameters = new byte[VocoderFrameSizes.HalfRateParameterBytes];
                HalfRateFecStatus status = privacyProcessor.ExtractAndProcessParameters(
                    codeword,
                    parameters);
                short[] decryptedSamples = new short[VocoderFrameSizes.PcmSamplesPerFrame];
                ((IHalfRateVocoderSession)vocoder).DecodeParameters(
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
            short[] samples = [];
            errors += decoder.Process(
                codeword,
                decoded => samples = decoded.ToArray());
            await playback.WriteAsync(samples, cancellationToken).ConfigureAwait(false);
            FramesDecoded++;
            hasDecodedVoiceInActiveStream = true;
        }

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
        for (int index = 0; index < frameCount; index++)
        {
            short[] samples = [];
            decoder.ProcessLost(decoded => samples = decoded.ToArray());
            await playback.WriteAsync(samples, cancellationToken).ConfigureAwait(false);
            FramesDecoded++;
        }
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
    }

    private void ClearPrivacyState()
    {
        privacyProcessor?.Dispose();
        privacyProcessor = null;
        privacyRequired = configuredPrivacyRequired;
        activeStreamId = 0;
    }
}
