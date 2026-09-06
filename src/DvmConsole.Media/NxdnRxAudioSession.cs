// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;
using DvmConsole.Core.Runtime;
using DvmConsole.Vocoder;

namespace DvmConsole.Media;

// Extracts the AMBE+2 codewords from selected 4800-baud NXDN frames and routes
// them through the mandatory software vocoder.
public sealed class NxdnRxAudioSession : IAsyncDisposable
{
    private const int MaximumConcealedPackets = 10;

    private readonly NxdnTrafficSelector selector;
    private readonly VoiceFrameDecoder decoder;
    private readonly IHalfRateVocoderSession? halfRateVocoder;
    private readonly IAudioPlayback playback;
    private readonly INxdnKeyResolver? keyResolver;
    private readonly string systemName;
    private readonly VoicePacketSequenceTracker sequenceTracker = new();
    private readonly NxdnSacchMessageCollector sacchCollector = new();
    private readonly byte[] ambe = new byte[NxdnVoicePacketCodec.AmbeBytes];
    private readonly short[] packetSamples = new short[
        NxdnVoicePacketCodec.CodewordsPerFrame * VocoderFrameSizes.PcmSamplesPerFrame];
    private readonly bool[] concealed = new bool[NxdnVoicePacketCodec.CodewordsPerFrame];
    private readonly short[] concealmentSamples = new short[
        MaximumConcealedPackets * NxdnVoicePacketCodec.CodewordsPerFrame *
        VocoderFrameSizes.PcmSamplesPerFrame];
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
        IRadioMediaFrame traffic,
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
            sacchCollector.Reset();
            ResetPrivacyForNewStream();
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

        Span<byte> sacchFragment = stackalloc byte[3];
        if (!NxdnVoicePacketCodec.TryParseVoicePacket(
                traffic.Payload,
                ambe,
                sacchFragment,
                out NxdnVoicePacketCodec.ParsedVoicePacket parsed))
        {
            MalformedPackets++;
            await ConcealCurrentPacketAsync(cancellationToken).ConfigureAwait(false);
            sacchCollector.Reset();
            InvalidatePrivacyAfterLoss();
            return 0;
        }

        if (parsed.FirstFacchMetadata is { } firstMetadata)
        {
            HandleCallMetadata(firstMetadata);
            if (parsed.SecondFacchMetadata is { } secondMetadata &&
                !CallMetadataMatches(firstMetadata, secondMetadata))
            {
                HandleCallMetadata(secondMetadata);
            }
            return 0;
        }

        NxdnVoicePacketCodec.CallMetadata? metadataAfterVoice = null;
        if (parsed.HasSacchFragment &&
            sacchCollector.TryAccept(
                parsed.SacchStructure,
                sacchFragment,
                out NxdnVoicePacketCodec.CallMetadata sacchMetadata))
        {
            if (sacchMetadata.MessageType == NxdnVoicePacketCodec.VoiceCallIvMessageType)
                metadataAfterVoice = sacchMetadata;
            else
                HandleCallMetadata(sacchMetadata);
        }

        int codewordCount = parsed.AmbeCodewordCount;
        if (codewordCount == 0)
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
        Array.Clear(concealed, 0, codewordCount);
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
        await WritePacketSegmentsAsync(packetSamples, concealed, codewordCount, cancellationToken)
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

        int frameCount = checked((int)Math.Min(lostPackets, MaximumConcealedPackets)) *
            lastVoiceCodewordCount;
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

    private ValueTask ConcealCurrentPacketAsync(CancellationToken cancellationToken)
        => ConcealLostPacketsAsync(1, cancellationToken);

    private async ValueTask WritePacketSegmentsAsync(
        short[] packetSamples,
        bool[] concealed,
        int codewordCount,
        CancellationToken cancellationToken)
    {
        int segmentStart = 0;
        while (segmentStart < codewordCount)
        {
            bool isConcealed = concealed[segmentStart];
            int segmentEnd = segmentStart + 1;
            while (segmentEnd < codewordCount && concealed[segmentEnd] == isConcealed)
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

    private void ResetPrivacyForNewStream()
    {
        ClearPrivacy(restoreConfigured: false);
        RestoreConfiguredPrivacy();
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
            ClearPrivacy(restoreConfigured: false);
            return;
        }
        if (metadata.MessageType == NxdnVoicePacketCodec.VoiceCallMessageType)
        {
            if (metadata.CipherType == 0)
            {
                ClearPrivacy(restoreConfigured: false);
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
