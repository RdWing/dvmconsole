// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;

namespace DvmConsole.Application;

/// <summary>Constructs the shared microphone, generated-audio and receive-transition graph.</summary>
internal sealed class ConsoleTransmitRuntime : IGeneratedAudioOperationPort
{
    private ITransmitKeyPort keys = null!;
    private ITransmitAudioBackendPort backend = null!;
    private Func<string?> outputRoute = null!;
    private SemaphoreSlim admission = null!;
    private Action validateGeneratedAdmission = null!;
    private Func<bool> monitorEnabled = null!;
    public ChannelTransmitCoordinator Microphone { get; private set; } = null!;
    public ToneTransmitCoordinator Tones { get; private set; } = null!;
    public LocalTonePlayer LocalTones { get; private set; } = null!;
    public TransmitAudioTransitionController AudioTransition { get; private set; } = null!;
    public TransmitLifecycleCoordinator Lifecycle { get; private set; } = null!;
    private Func<CancellationToken, ValueTask<IAudioPlayback>>? openSharedOutput;
    public GeneratedAudioMonitor Monitor { get; private set; } = null!;
    public ManualTransmitCoordinator Manual { get; private set; } = null!;
    public ManualTransmitSession Session { get; private set; } = null!;
    public GeneratedAudioOperation GeneratedOperation { get; private set; } = null!;

    // Hosts keep the established construction stages and global disposal order.
    // Each successfully constructed service remains available for partial rollback.
    public void InitializeMicrophone(ITransmitKeyPort keys, AudioInputProcessingOptions options,
        ITransmitAudioBackendPort backend, ITransmitSampleObservationPort samples)
    {
        if (Microphone is not null) throw new InvalidOperationException("Microphone runtime is already initialized.");
        this.keys = keys ?? throw new ArgumentNullException(nameof(keys));
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
        Microphone = new ChannelTransmitCoordinator(keys, options, backend, samples);
    }

    public void InitializeTones(Func<string?> outputRoute, Func<ChannelId, bool, uint, ValueTask> observeState,
        Func<CancellationToken, ValueTask<IAudioPlayback>>? openSharedOutput = null)
    {
        if (Microphone is null || Tones is not null) throw new InvalidOperationException("Tone initialization is out of order.");
        this.outputRoute = outputRoute ?? throw new ArgumentNullException(nameof(outputRoute));
        Tones = new ToneTransmitCoordinator(keys.P25, backend.CreateVocoderBackend, keys.Dmr, keys.Nxdn, observeState);
        this.openSharedOutput = openSharedOutput;
        LocalTones = new LocalTonePlayer(backend.CreateAudioBackend, outputRoute, openSharedOutput: openSharedOutput);
    }

    public void InitializeTransitions(ChannelReceiveAudioCoordinator receiveAudio,
        ITransmitReceiveRoutePort routes, ITransmitReceiveMutePort mute,
        ITransmitAudioPresentationPort presentation, ITransmitAudioGate gate,
        ITransmitLifecyclePresentation lifecycle, SemaphoreSlim admission, ManualTransmitPolicy manual,
        Action validateGeneratedAdmission, Func<bool> monitorEnabled, ManualTransmitStatePorts state)
    {
        InitializeManualTransitions(receiveAudio, routes, mute, presentation, gate, lifecycle, admission, manual, state);
        InitializeGeneratedOperations(admission, validateGeneratedAdmission, monitorEnabled);
    }

    public void InitializeManualTransitions(ChannelReceiveAudioCoordinator receiveAudio,
        ITransmitReceiveRoutePort routes, ITransmitReceiveMutePort mute,
        ITransmitAudioPresentationPort presentation, ITransmitAudioGate gate,
        ITransmitLifecyclePresentation lifecycle, SemaphoreSlim admission, ManualTransmitPolicy manual,
        ManualTransmitStatePorts state)
    {
        if (LocalTones is null || AudioTransition is not null) throw new InvalidOperationException("Transmit transition initialization is out of order.");
        AudioTransition = new TransmitAudioTransitionController(routes, mute,
            new TransmitPermitTonePort(LocalTones), presentation, gate);
        Session = new ManualTransmitSession(manual, lifecycle, state, Microphone);
        Lifecycle = new TransmitLifecycleCoordinator(new TransmitLifecycleTransport(Microphone),
            new TransmitLifecycleAudio(receiveAudio, LocalTones, AudioTransition), Session);
        Manual = new ManualTransmitCoordinator(admission, Session, Lifecycle.StartAsync, Lifecycle.StopAsync);
    }

    public IChannelTransmitInputPort? ChannelInput { private get; set; }

    public Task<bool> BeginChannelAsync(ChannelId id, Action? starting = null, CancellationToken cancellationToken = default)
        => ChannelInput is { } input
            ? BeginObservedChannelAsync(id, input, starting, cancellationToken)
            : Session.BeginChannelAsync(id, Manual, starting, cancellationToken);

    private async Task<bool> BeginObservedChannelAsync(ChannelId id, IChannelTransmitInputPort input,
        Action? starting, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Session.IsInputSuppressed) return false;
        input.Requested(id);
        bool observed = false;
        bool started = await Session.BeginChannelAsync(id, Manual, () =>
        {
            starting?.Invoke();
            input.Starting();
            observed = true;
        }, cancellationToken).ConfigureAwait(false);
        if (started && observed) input.Started();
        else if (!started && Microphone.ActiveChannel is null) input.Cleared();
        return started;
    }

    public Task EndChannelAsync(ChannelId id) => Session.EndChannelAsync(id, Manual);

    public void InitializeGeneratedOperations(SemaphoreSlim admission, Action validateAdmission, Func<bool> monitorEnabled)
    {
        if (Manual is null || GeneratedOperation is not null)
            throw new InvalidOperationException("Generated operation initialization is out of order.");
        this.admission = admission ?? throw new ArgumentNullException(nameof(admission));
        validateGeneratedAdmission = validateAdmission ?? throw new ArgumentNullException(nameof(validateAdmission));
        this.monitorEnabled = monitorEnabled ?? throw new ArgumentNullException(nameof(monitorEnabled));
        Monitor = new GeneratedAudioMonitor(backend.CreateAudioBackend, outputRoute, openSharedOutput: openSharedOutput);
        GeneratedOperation = new GeneratedAudioOperation(this);
    }

    async ValueTask<IAsyncDisposable> IGeneratedAudioOperationPort.EnterTransmitAsync(CancellationToken token)
    {
        await admission.WaitAsync(token).ConfigureAwait(false);
        return new GeneratedAdmission(admission);
    }

    void IGeneratedAudioOperationPort.Validate(IReadOnlyList<TransmitTarget> targets)
    {
        validateGeneratedAdmission();
        if (Microphone.ActiveChannel is not null)
            throw new InvalidOperationException("Release PTT before sending generated audio.");
        ToneTransmitCoordinator.ValidateTargets(targets);
    }

    bool IGeneratedAudioOperationPort.MonitorEnabled => monitorEnabled();
    Task IGeneratedAudioOperationPort.MuteReceiveAsync()
        => AudioTransition.MuteReceiveAudioAsync("RX audio muted while sending generated audio.");
    Task IGeneratedAudioOperationPort.RestoreReceiveAsync() => AudioTransition.RestoreSuspendedAudioAsync();
    Task IGeneratedAudioOperationPort.MonitorAsync(ReadOnlyMemory<short> samples, CancellationToken token)
        => Monitor.PlayAsync(samples, token);
    Task IGeneratedAudioOperationPort.TransmitAsync(IReadOnlyList<TransmitTarget> targets,
        ReadOnlyMemory<short> samples, GeneratedToneSequence? sequence, CancellationToken token)
        => sequence is null ? Tones.SendAsync(targets, samples, token)
            : Tones.SendAsync(targets, sequence, samples, token);

    private sealed class GeneratedAdmission(SemaphoreSlim gate) : IAsyncDisposable
    {
        private int released;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) gate.Release();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Registers retirement at the host's established position in global service order.</summary>
    public void RegisterCoordinatorOwnership(ConsoleSessionServices services, string name,
        SemaphoreSlim commandGate, SemaphoreSlim admissionGate, Func<Task> prepareMicrophoneRetirement,
        Action? beforeRetirement = null)
        => services.Transmit.Register(name, async () =>
        {
            beforeRetirement?.Invoke();
            await DisposeCoordinatorsAsync(commandGate, admissionGate, prepareMicrophoneRetirement).ConfigureAwait(false);
        });

    /// <summary>Rechecks disconnected-system ownership under both gates before releasing the whole manual call.</summary>
    public async Task StopForDisconnectedSystemAsync(IReadOnlyList<ChannelId> systemChannels,
        ConsoleTransmitChannelDirectory channels, SemaphoreSlim commandGate, SemaphoreSlim admissionGate,
        Action clearInputLatches, Action clearIdleOwnership, string status)
    {
        await commandGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await admissionGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!channels.OwnsActiveTransmit(systemChannels, Microphone.ActiveChannels)) return;
                clearInputLatches();
                ChannelId[] active = channels.CaptureShutdownChannels(Microphone.ActiveChannels);
                if (active.Length == 0) clearIdleOwnership();
                else await Lifecycle.StopAsync(active, status).ConfigureAwait(false);
            }
            finally { admissionGate.Release(); }
        }
        finally { commandGate.Release(); }
    }

    /// <summary>Releases every transmitter for full shutdown, preserving gate and failure ordering.</summary>
    public async Task ReleaseForShutdownAsync(
        SemaphoreSlim commandGate, SemaphoreSlim admissionGate, ConsoleTransmitChannelDirectory channels,
        Func<CancellationToken, ValueTask> stopInputs, Action clearInputLatches,
        CancellationToken cancellationToken = default)
    {
        Manual?.CancelStartup();
        var cleanup = new AsyncCleanup();
        if (GeneratedOperation is not null)
            await cleanup.RunTaskAsync(() => GeneratedOperation.CancelAndDrainAsync()).ConfigureAwait(false);
        await cleanup.RunTaskAsync(() => stopInputs(cancellationToken).AsTask()).ConfigureAwait(false);
        bool commandEntered = false;
        bool admissionEntered = false;
        try
        {
            await commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            commandEntered = true;
            await admissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            admissionEntered = true;
            clearInputLatches();
            ChannelId[] active = channels.CaptureShutdownChannels(Microphone?.ActiveChannels ?? []);
            if (active.Length > 0)
                await cleanup.RunTaskAsync(() => Lifecycle.StopAsync(active,
                    "Transmission stopped during application shutdown.")).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            cleanup.Capture(exception);
        }
        finally
        {
            if (admissionEntered) admissionGate.Release();
            if (commandEntered) commandGate.Release();
        }
        cleanup.ThrowIfFailed();
    }

    // Cancel generated operations before taking their admission gate. Hosts only
    // supply their warm-microphone presentation detachment and idle wait.
    public async Task DisposeCoordinatorsAsync(
        SemaphoreSlim pttStateChangeLock, SemaphoreSlim transmitAdmissionGate,
        Func<Task> prepareMicrophoneRetirement)
    {
        var cleanup = new AsyncCleanup();
        // Cancel and join operations before taking the admission gate they own.
        if (GeneratedOperation is not null)
            await cleanup.RunTaskAsync(() => GeneratedOperation.DisposeAsync().AsTask()).ConfigureAwait(false);
        bool pttGateEntered = false;
        bool admissionEntered = false;
        try
        {
            await pttStateChangeLock.WaitAsync().ConfigureAwait(false);
            pttGateEntered = true;
            await transmitAdmissionGate.WaitAsync().ConfigureAwait(false);
            admissionEntered = true;
        }
        catch (Exception exception)
        {
            cleanup.Capture(exception);
        }

        if (pttGateEntered)
        {
            try
            {
                if (Tones is not null)
                {
                    await cleanup.RunTaskAsync(
                        () => Tones.DisposeAsync().AsTask()).ConfigureAwait(false);
                }
                if (Monitor is not null)
                {
                    await cleanup.RunTaskAsync(
                        () => Monitor.DisposeAsync().AsTask()).ConfigureAwait(false);
                }
                if (LocalTones is not null)
                {
                    await cleanup.RunTaskAsync(
                        () => LocalTones.DisposeAsync().AsTask()).ConfigureAwait(false);
                }
                await cleanup.RunTaskAsync(prepareMicrophoneRetirement).ConfigureAwait(false);
                if (Microphone is not null)
                {
                    await cleanup.RunTaskAsync(
                        () => Microphone.DisposeAsync().AsTask()).ConfigureAwait(false);
                }
            }
            finally
            {
                if (admissionEntered)
                    transmitAdmissionGate.Release();
                pttStateChangeLock.Release();
            }
        }

        cleanup.ThrowIfFailed();
    }

}
