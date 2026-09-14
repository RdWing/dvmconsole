// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;
using DvmConsole.Media;
using DvmConsole.Vocoder;

namespace DvmConsole.Application;

/// <summary>Owns construction and ordered processing of the shared patch media graph.</summary>
internal sealed class ConsolePatchRuntime
{
    private PatchSourceReceivePipeline pipeline = null!;
    private Action<Exception> reportDecodeFailure = null!;
    public PatchForwardingCoordinator Forwarding { get; private set; } = null!;
    public PatchSourceDecodeCoordinator Decoder { get; private set; } = null!;
    public ChannelReceiveWorkQueue Work { get; private set; } = null!;
    private readonly object admissionSync = new();
    private readonly Dictionary<ChannelId, uint> interruptedStreams = [];
    private long generation;
    private bool accepting = true;
    private Task pause = Task.CompletedTask;

    public bool Enqueue(ChannelId channel, IRadioMediaFrame traffic, long? timestamp)
    {
        lock (admissionSync)
        {
            if (!accepting) return false;
            if (interruptedStreams.TryGetValue(channel, out uint interrupted))
            {
                if (traffic.StreamId == interrupted) return false;
                interruptedStreams.Remove(channel);
            }
            Work.Start(channel);
            return Work.Enqueue(channel, RadioMediaIngressFrame.FromFrame(traffic, timestamp)
                with
            { AdmissionGeneration = generation });
        }
    }

    public Task PauseAsync()
    {
        lock (admissionSync)
        {
            if (!accepting) return pause;
            accepting = false;
            generation++;
            Task forwarding = Forwarding.PauseForwardingAsync();
            return pause = Task.WhenAll(Decoder.ActiveChannels.Select(Work.StopAsync).Append(forwarding));
        }
    }

    public void Resume(IEnumerable<KeyValuePair<ChannelId, uint>> currentStreams)
    {
        lock (admissionSync)
        {
            if (accepting) return;
            if (!pause.IsCompletedSuccessfully)
                throw new InvalidOperationException("Patch receive work must finish stopping before resuming.");
            // Ingress has already selected each channel's current physical stream.
            // Retain at most one interrupted identity per configured channel.
            foreach (var pair in currentStreams)
                if (pair.Value != 0) interruptedStreams[pair.Key] = pair.Value;
            Forwarding.ResumeForwarding();
            accepting = true;
        }
    }

    // Register before construction so rollback owns every successfully created
    // service. Reverse retirement joins source work before decoder and forwarding.
    public void RegisterOwnership(ConsoleSessionServices services, Action? detachPresentation = null)
    {
        services.Patch.Register("routing", async () =>
        {
            var cleanup = new AsyncCleanup();
            if (detachPresentation is not null) cleanup.Run(detachPresentation);
            if (Forwarding is not null)
                await cleanup.RunTaskAsync(() => Forwarding.DisposeAsync().AsTask()).ConfigureAwait(false);
            cleanup.ThrowIfFailed();
        });
        services.Patch.Register("source-decode", () => Decoder is null ? ValueTask.CompletedTask : Decoder.DisposeAsync());
        services.Patch.Register("source-receive-work", () => Work is null ? ValueTask.CompletedTask : Work.DisposeAsync());
    }

    public void Initialize(IEnumerable<IRadioTrafficEndpoint> systems, ITransmitKeyPort keys,
        Func<IVocoderBackend> createTransmitVocoder, Func<IVocoderBackend> createSourceVocoder,
        Func<ChannelId, TransmitChannelDescriptor?> resolveChannel,
        Func<DmrReceiveKeyPolicy> receiveKeyPolicy, bool sourceIdPassthrough,
        Action<PatchForwardingDiagnostic> diagnosticObserver,
        Func<ChannelId, RadioMediaProtocol, ReceiveJitterBufferProfile> jitterProfile,
        Action<IReadOnlyList<ReceiveWorkerShutdownDiagnostic>> shutdownObserver,
        Action<Exception> reportDecodeFailure)
    {
        if (Forwarding is not null) throw new InvalidOperationException("Patch runtime is already initialized.");
        this.reportDecodeFailure = reportDecodeFailure ?? throw new ArgumentNullException(nameof(reportDecodeFailure));
        Forwarding = new PatchForwardingCoordinator(systems, keys.P25, createTransmitVocoder,
            keys.Dmr, keys.Nxdn, diagnosticObserver, resolveChannel)
        {
            SourceIdPassthrough = sourceIdPassthrough
        };
        Decoder = new PatchSourceDecodeCoordinator(keys.P25, Forwarding.ObserveDecodedSamples,
            createSourceVocoder, keys.Dmr, keys.Nxdn, receiveKeyPolicy);
        pipeline = new PatchSourceReceivePipeline(Decoder, Forwarding);
        Work = ChannelReceiveWorkQueue.CreateWithIngressTiming(async (channel, frame, token) =>
        {
            lock (admissionSync)
                if (!accepting || frame.AdmissionGeneration != generation) return default;
            await ProcessAsync(channel, frame.Traffic, token).ConfigureAwait(false);
            return default;
        }, getJitterBufferProfile: jitterProfile,
            shutdownDelayObserver: shutdownObserver);
    }

    public async Task RebuildDecodersAsync(IEnumerable<ChannelId> configuredChannels,
        IReadOnlyList<ReceiveChannelDescriptor> activeSources, CancellationToken cancellationToken = default)
    {
        foreach (ChannelId channel in configuredChannels)
            await Work.StopAsync(channel).ConfigureAwait(false);
        await Decoder.StopAllAsync(cancellationToken).ConfigureAwait(false);
        await Decoder.ApplyChannelsAsync(activeSources, cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessAsync(ChannelId channel, IRadioMediaFrame traffic,
        CancellationToken cancellationToken)
    {
        try
        {
            await pipeline.ProcessAsync(channel, traffic, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            reportDecodeFailure(exception);
        }
    }
}
