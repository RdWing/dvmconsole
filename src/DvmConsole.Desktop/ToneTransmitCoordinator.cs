using Avalonia.Threading;
using DvmConsole.Audio;
using DvmConsole.Core.Runtime;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Vocoder;

namespace DvmConsole.Desktop;

// Sends generated 8 kHz PCM tone sequences through the selected channel's
// normal DMR, P25, NXDN, or analog call lifecycle. It deliberately does not open a
// microphone; generated audio is paced as 20 ms media frames instead.
public sealed class ToneTransmitCoordinator : IAsyncDisposable
{
    private readonly IP25KeyResolver? p25KeyResolver;
    private readonly IDmrKeyResolver? dmrKeyResolver;
    private readonly INxdnKeyResolver? nxdnKeyResolver;
    private readonly Func<IVocoderBackend> createVocoderBackend;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;
    private bool sending;

    public ToneTransmitCoordinator(
        IP25KeyResolver? p25KeyResolver = null,
        Func<IVocoderBackend>? createVocoderBackend = null,
        IDmrKeyResolver? dmrKeyResolver = null,
        INxdnKeyResolver? nxdnKeyResolver = null)
    {
        this.p25KeyResolver = p25KeyResolver;
        this.dmrKeyResolver = dmrKeyResolver;
        this.nxdnKeyResolver = nxdnKeyResolver;
        this.createVocoderBackend = createVocoderBackend ??
            (() => new SoftwareVocoderBackend());
    }

    public bool IsSending => sending;

    public async Task SendAsync(
        ChannelViewModel channel,
        IFneTrafficEndpoint system,
        ReadOnlyMemory<short> samples,
        CancellationToken cancellationToken = default)
        => await SendAsync([new TransmitTarget(channel, system)], samples, cancellationToken).ConfigureAwait(false);

    public async Task SendAsync(
        IEnumerable<TransmitTarget> targets,
        ReadOnlyMemory<short> samples,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (samples.IsEmpty)
            throw new ArgumentException("Tone audio cannot be empty.", nameof(samples));
        ObjectDisposedException.ThrowIf(disposed, this);

        TransmitTarget[] requested = targets
            .GroupBy(target => target.Channel)
            .Select(group => group.First())
            .ToArray();
        if (requested.Length == 0)
            throw new InvalidOperationException("Select at least one transmit-capable channel.");

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ValidateTargets(requested);

            sending = true;
            await Task.WhenAll(requested.Select(target => SendCoreAsync(
                target.Channel,
                target.System,
                target.System.SourceId!.Value,
                samples,
                sequence: null,
                cancellationToken))).ConfigureAwait(false);
        }
        finally
        {
            sending = false;
            gate.Release();
        }
    }

    public async Task SendAsync(
        IEnumerable<TransmitTarget> targets,
        GeneratedToneSequence sequence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(sequence);
        ObjectDisposedException.ThrowIf(disposed, this);

        TransmitTarget[] requested = targets
            .GroupBy(target => target.Channel)
            .Select(group => group.First())
            .ToArray();
        if (requested.Length == 0)
            throw new InvalidOperationException("Select at least one transmit-capable channel.");

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ValidateTargets(requested);

            sending = true;
            await Task.WhenAll(requested.Select(target => SendCoreAsync(
                target.Channel,
                target.System,
                target.System.SourceId!.Value,
                samples: default,
                sequence,
                cancellationToken))).ConfigureAwait(false);
        }
        finally
        {
            sending = false;
            gate.Release();
        }
    }

    private static void ValidateTargets(IEnumerable<TransmitTarget> targets)
    {
        foreach (TransmitTarget target in targets)
        {
            if (!target.Channel.CanTransmit)
                throw new InvalidOperationException($"{target.Channel.Name} cannot transmit generated audio.");
            if (!target.System.Channels.Contains(target.Channel))
                throw new InvalidOperationException($"{target.Channel.Name} does not belong to FNE system '{target.System.Name}'.");
            if (!target.System.IsConnected)
                throw new InvalidOperationException($"The FNE system '{target.System.Name}' is not connected.");
            if (target.System.SourceId is not uint sourceId || sourceId == 0)
                throw new InvalidOperationException($"The FNE system '{target.System.Name}' has no valid transmit RID.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;

        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            disposed = true;
        }
        finally
        {
            gate.Release();
            gate.Dispose();
        }
    }

    private async Task SendCoreAsync(
        ChannelViewModel channel,
        IFneTrafficEndpoint system,
        uint sourceId,
        ReadOnlyMemory<short> samples,
        GeneratedToneSequence? sequence,
        CancellationToken cancellationToken)
    {
        ChannelRuntimeDefinition definition = ChannelTransmitDefinitionFactory.Create(channel);
        P25TxEncryptionOptions? encryption = ChannelTransmitDefinitionFactory.CreateEncryptionOptions(
            channel,
            definition,
            p25KeyResolver);
        DmrPrivacyOptions? dmrPrivacy = ChannelTransmitDefinitionFactory.CreateDmrPrivacyOptions(
            channel,
            definition,
            dmrKeyResolver);
        NxdnPrivacyOptions? nxdnPrivacy = ChannelTransmitDefinitionFactory.CreateNxdnPrivacyOptions(
            channel,
            definition,
            nxdnKeyResolver);
        IVocoderBackend? vocoderBackend = null;
        IVocoderSession? vocoderSession = null;
        PatchTransmitSession? session = null;

        try
        {
            if (ChannelProtocolMediaMapper.RequiresVocoder(definition.Protocol))
            {
                vocoderBackend = createVocoderBackend();
                vocoderSession = vocoderBackend.CreateSession(
                    ChannelProtocolMediaMapper.ToVocoderMode(definition.Protocol));
            }

            uint streamId = system.CreateStreamId();
            session = new PatchTransmitSession(
                definition,
                sourceId,
                streamId,
                vocoderSession,
                (payload, sequence, stream) => system.SendTraffic(
                    ChannelProtocolMediaMapper.ToTrafficProtocol(definition.Protocol),
                    payload.Span,
                    sequence,
                    stream),
                encryption,
                dmrPrivacy,
                nxdnPrivacy);
            vocoderSession = null;
            session.Start();
            await SetTransmitStateAsync(channel, enabled: true, streamId).ConfigureAwait(false);

            try
            {
                var cadence = new TransmitFrameCadence();
                if (sequence is not null && definition.Protocol == ChannelProtocol.P25)
                {
                    await SendP25SequenceAsync(session, sequence, cadence, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    ReadOnlyMemory<short> pcm = sequence?.RenderPcm() ?? samples;
                    for (int offset = 0; offset < pcm.Length; offset += VocoderFrameSizes.PcmSamplesPerFrame)
                    {
                        await cadence.WaitForNextFrameAsync(cancellationToken).ConfigureAwait(false);
                        int count = Math.Min(VocoderFrameSizes.PcmSamplesPerFrame, pcm.Length - offset);
                        if (count == VocoderFrameSizes.PcmSamplesPerFrame)
                        {
                            session.Process(pcm.Span.Slice(offset, count));
                        }
                        else
                        {
                            var finalFrame = new short[VocoderFrameSizes.PcmSamplesPerFrame];
                            pcm.Span.Slice(offset, count).CopyTo(finalFrame);
                            session.Process(finalFrame);
                        }
                    }
                }

                await session.EndAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await SetTransmitStateAsync(channel, enabled: false).ConfigureAwait(false);
            }
        }
        finally
        {
            if (session is not null)
            {
                try
                {
                    if (session.IsStarted && !session.IsEnded)
                        await session.EndAsync(CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    session.Dispose();
                }
            }

            vocoderSession?.Dispose();
            vocoderBackend?.Dispose();
        }
    }

    private static async Task SendP25SequenceAsync(
        PatchTransmitSession session,
        GeneratedToneSequence sequence,
        TransmitFrameCadence cadence,
        CancellationToken cancellationToken)
    {
        short[] pcm = sequence.RenderPcm();
        int pcmOffset = 0;
        foreach (GeneratedToneStep step in sequence.Steps)
        {
            for (int frameIndex = 0; frameIndex < step.FrameCount; frameIndex++)
            {
                await cadence.WaitForNextFrameAsync(cancellationToken).ConfigureAwait(false);
                if (step.Kind == GeneratedToneStepKind.SingleTone)
                    session.ProcessP25SingleTone(step.FrequencyHz);
                else
                    session.Process(pcm.AsSpan(pcmOffset, VocoderFrameSizes.PcmSamplesPerFrame));
                pcmOffset += VocoderFrameSizes.PcmSamplesPerFrame;
            }
        }
    }

    private static async Task SetTransmitStateAsync(
        ChannelViewModel channel,
        bool enabled,
        uint streamId = 0)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            channel.SetTransmitEnabled(enabled, streamId);
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() => channel.SetTransmitEnabled(enabled, streamId));
    }
}
