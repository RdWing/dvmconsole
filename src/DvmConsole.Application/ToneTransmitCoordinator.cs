// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;
using DvmConsole.Core.Runtime;
using DvmConsole.Media;
using DvmConsole.Vocoder;

namespace DvmConsole.Application;

public sealed class ToneSendingChangedEventArgs(bool isSending) : EventArgs
{
    public bool IsSending { get; } = isSending;
}

// Sends generated 8 kHz PCM tone sequences through the selected channel's
// normal DMR, P25, NXDN, or analog call lifecycle. It deliberately does not
// open a microphone. Digital PCM is prebuffered in complete protocol packets
// so the protocol pacer cannot be starved by a delayed 20 ms producer wake;
// analog audio retains its 20 ms packet cadence.
public sealed class ToneTransmitCoordinator : IAsyncDisposable
{
    internal const double DmrNxdnToneTargetDbfs = -25;
    private const int PrefetchedProtocolPackets = 2;
    private static readonly TimeSpan PcmFrameInterval = TimeSpan.FromMilliseconds(20);
    private readonly IP25KeyResolver? p25KeyResolver;
    private readonly IDmrKeyResolver? dmrKeyResolver;
    private readonly INxdnKeyResolver? nxdnKeyResolver;
    private readonly Func<IVocoderBackend> createVocoderBackend;
    private readonly Func<ChannelId, bool, uint, ValueTask> stateObserver;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly OrderedNotificationDispatcher notifications = new();
    private readonly AsyncDisposal disposal = new();
    private int lifecycleState;
    private bool sending;

    public event EventHandler<ToneSendingChangedEventArgs>? SendingChanged;

    public ToneTransmitCoordinator(
        IP25KeyResolver? p25KeyResolver = null,
        Func<IVocoderBackend>? createVocoderBackend = null,
        IDmrKeyResolver? dmrKeyResolver = null,
        INxdnKeyResolver? nxdnKeyResolver = null,
        Func<ChannelId, bool, uint, ValueTask>? stateObserver = null,
        TimeProvider? timeProvider = null)
    {
        this.p25KeyResolver = p25KeyResolver;
        this.dmrKeyResolver = dmrKeyResolver;
        this.nxdnKeyResolver = nxdnKeyResolver;
        this.createVocoderBackend = createVocoderBackend ??
            (() => throw new InvalidOperationException(
                "A vocoder backend factory is required for digital generated audio."));
        this.stateObserver = stateObserver ?? ((_, _, _) => ValueTask.CompletedTask);
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsSending => sending;

    public async Task SendAsync(
        TransmitChannelDescriptor channel,
        IRadioTrafficEndpoint system,
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
        ThrowIfDisposingOrDisposed();

        TransmitTarget[] requested = targets
            .GroupBy(target => target.Channel.Id)
            .Select(group => group.First())
            .ToArray();
        if (requested.Length == 0)
            throw new InvalidOperationException("Select at least one transmit-capable channel.");

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposingOrDisposed();
            ValidateTargets(requested);

            SetSending(true);
            await Task.WhenAll(requested.Select(target => SendCoreAsync(
                target.Channel,
                target.System,
                target.System.SourceId!.Value,
                samples,
                digitalToneSamples: null,
                sequence: null,
                cancellationToken))).ConfigureAwait(false);
        }
        finally
        {
            SetSending(false);
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
        ThrowIfDisposingOrDisposed();
        await SendAsync(
            targets,
            sequence,
            sequence.RenderPcm(),
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task SendAsync(
        IEnumerable<TransmitTarget> targets,
        GeneratedToneSequence sequence,
        ReadOnlyMemory<short> renderedSamples,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(sequence);
        ThrowIfDisposingOrDisposed();
        ValidateRenderedSamples(sequence, renderedSamples);

        TransmitTarget[] requested = targets
            .GroupBy(target => target.Channel.Id)
            .Select(group => group.First())
            .ToArray();
        if (requested.Length == 0)
            throw new InvalidOperationException("Select at least one transmit-capable channel.");

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposingOrDisposed();
            ValidateTargets(requested);
            ReadOnlyMemory<short> digitalToneSamples = sequence.RenderPcmAtRms(
                DmrNxdnToneTargetDbfs);

            SetSending(true);
            await Task.WhenAll(requested.Select(target => SendCoreAsync(
                target.Channel,
                target.System,
                target.System.SourceId!.Value,
                renderedSamples,
                digitalToneSamples,
                sequence,
                cancellationToken))).ConfigureAwait(false);
        }
        finally
        {
            SetSending(false);
            gate.Release();
        }
    }

    private static void ValidateRenderedSamples(
        GeneratedToneSequence sequence,
        ReadOnlyMemory<short> renderedSamples)
    {
        int expectedSampleCount = checked(
            sequence.FrameCount * VocoderFrameSizes.PcmSamplesPerFrame);
        if (renderedSamples.Length != expectedSampleCount)
        {
            throw new ArgumentException(
                $"Rendered tone audio must contain exactly {expectedSampleCount} samples.",
                nameof(renderedSamples));
        }
    }

    private void SetSending(bool value)
    {
        if (Volatile.Read(ref sending) == value)
            return;

        Volatile.Write(ref sending, value);
        EventHandler<ToneSendingChangedEventArgs>? observers = SendingChanged;
        var args = new ToneSendingChangedEventArgs(value);
        notifications.Enqueue(() => NotifySendingChanged(observers, args));
    }

    internal Task DrainNotificationsAsync() => notifications.DrainAsync();

    private void NotifySendingChanged(
        EventHandler<ToneSendingChangedEventArgs>? observers,
        ToneSendingChangedEventArgs args)
    {
        if (observers is null)
            return;
        foreach (EventHandler<ToneSendingChangedEventArgs> observer in observers.GetInvocationList())
        {
            try
            {
                observer(this, args);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceError(
                    "Tone state observer failed: {0}",
                    exception);
            }
        }
    }

    internal static void ValidateTargets(IEnumerable<TransmitTarget> targets)
    {
        foreach (TransmitTarget target in targets)
        {
            TransmitTargetPolicy.ThrowIfUnavailable(target.Channel, target.System);
            if (!target.System.ChannelIds.Contains(target.Channel.Id))
                throw new InvalidOperationException($"{target.Channel.Name} does not belong to FNE system '{target.System.Name}'.");
            if (!target.System.IsConnected)
                throw new InvalidOperationException($"The FNE system '{target.System.Name}' is not connected.");
            if (target.System.SourceId is not uint sourceId || sourceId == 0)
                throw new InvalidOperationException($"The FNE system '{target.System.Name}' has no valid transmit RID.");
        }
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.CompareExchange(ref lifecycleState, 1, 0);
        return disposal.RunAsync(DisposeCoreAsync);
    }

    private async Task DisposeCoreAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Volatile.Write(ref lifecycleState, 2);
        }
        finally
        {
            gate.Release();
        }

        await notifications.DisposeAsync().ConfigureAwait(false);
    }

    private void ThrowIfDisposingOrDisposed()
        => ObjectDisposedException.ThrowIf(
            Volatile.Read(ref lifecycleState) != 0,
            this);

    private async Task SendCoreAsync(
        TransmitChannelDescriptor channel,
        IRadioTrafficEndpoint system,
        uint sourceId,
        ReadOnlyMemory<short> samples,
        ReadOnlyMemory<short>? digitalToneSamples,
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
                    payload,
                    sequence,
                    stream),
                encryption,
                dmrPrivacy,
                nxdnPrivacy);
            vocoderSession = null;
            session.Start();
            await stateObserver(channel.Id, true, streamId).ConfigureAwait(false);

            try
            {
                ReadOnlyMemory<short> transmittedSamples =
                    digitalToneSamples is { } leveledToneSamples &&
                    definition.Protocol is ChannelProtocol.Dmr or ChannelProtocol.Nxdn
                        ? leveledToneSamples
                        : samples;
                if (sequence is not null && definition.Protocol == ChannelProtocol.P25)
                {
                    await SendP25SequenceAsync(
                        session,
                        sequence,
                        transmittedSamples,
                        CreateGeneratedAudioCadence(definition.Protocol),
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await SendPcmAsync(
                        session,
                        definition.Protocol,
                        transmittedSamples,
                        cancellationToken).ConfigureAwait(false);
                }

                await session.EndAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await stateObserver(channel.Id, false, 0).ConfigureAwait(false);
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
        ReadOnlyMemory<short> renderedSamples,
        TransmitFrameCadence cadence,
        CancellationToken cancellationToken)
    {
        int pcmOffset = 0;
        int frameIndex = 0;
        foreach (GeneratedToneStep step in sequence.Steps)
        {
            for (int stepFrameIndex = 0; stepFrameIndex < step.FrameCount; stepFrameIndex++)
            {
                await WaitForGeneratedBatchAsync(
                    cadence,
                    frameIndex,
                    FramesPerProtocolPacket(ChannelProtocol.P25),
                    cancellationToken).ConfigureAwait(false);
                if (step.Kind == GeneratedToneStepKind.SingleTone)
                    session.ProcessP25SingleTone(step.FrequencyHz);
                else
                    session.Process(renderedSamples.Span.Slice(
                        pcmOffset,
                        VocoderFrameSizes.PcmSamplesPerFrame));
                pcmOffset += VocoderFrameSizes.PcmSamplesPerFrame;
                frameIndex++;
            }
        }
    }

    private async Task SendPcmAsync(
        PatchTransmitSession session,
        ChannelProtocol protocol,
        ReadOnlyMemory<short> samples,
        CancellationToken cancellationToken)
    {
        int framesPerPacket = FramesPerProtocolPacket(protocol);
        TransmitFrameCadence cadence = CreateGeneratedAudioCadence(protocol);
        int frameIndex = 0;
        for (int offset = 0; offset < samples.Length; offset += VocoderFrameSizes.PcmSamplesPerFrame)
        {
            await WaitForGeneratedBatchAsync(
                cadence,
                frameIndex,
                framesPerPacket,
                cancellationToken).ConfigureAwait(false);
            int count = Math.Min(VocoderFrameSizes.PcmSamplesPerFrame, samples.Length - offset);
            if (count == VocoderFrameSizes.PcmSamplesPerFrame)
            {
                session.Process(samples.Span.Slice(offset, count));
            }
            else
            {
                var finalFrame = new short[VocoderFrameSizes.PcmSamplesPerFrame];
                samples.Span.Slice(offset, count).CopyTo(finalFrame);
                session.Process(finalFrame);
            }
            frameIndex++;
        }
    }

    private TransmitFrameCadence CreateGeneratedAudioCadence(ChannelProtocol protocol)
    {
        int framesPerPacket = FramesPerProtocolPacket(protocol);
        TimeSpan interval = TimeSpan.FromTicks(PcmFrameInterval.Ticks * framesPerPacket);
        return protocol == ChannelProtocol.Analog
            ? new TransmitFrameCadence(interval, timeProvider)
            : TransmitFrameCadence.StartAfterInterval(interval, timeProvider);
    }

    private static async ValueTask WaitForGeneratedBatchAsync(
        TransmitFrameCadence cadence,
        int frameIndex,
        int framesPerPacket,
        CancellationToken cancellationToken)
    {
        if (framesPerPacket == 1)
        {
            await cadence.WaitForNextFrameAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (frameIndex % framesPerPacket != 0)
            return;

        int packetIndex = frameIndex / framesPerPacket;
        if (packetIndex >= PrefetchedProtocolPackets)
            await cadence.WaitForNextFrameAsync(cancellationToken).ConfigureAwait(false);
    }

    private static int FramesPerProtocolPacket(ChannelProtocol protocol)
        => protocol switch
        {
            ChannelProtocol.Dmr => 3,
            ChannelProtocol.Nxdn => 4,
            ChannelProtocol.P25 => 9,
            ChannelProtocol.Analog => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, null)
        };

}
