// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;
using DvmConsole.Core.Runtime;
using DvmConsole.Media;
using DvmConsole.Operations;
using DvmConsole.Vocoder;

namespace DvmConsole.Application;

public sealed record TransmitTarget(TransmitChannelDescriptor Channel, IRadioTrafficEndpoint System);

public delegate void BorrowedTransmitSamplesObserver(
    ChannelId channelId,
    uint streamId,
    uint sourceId,
    ReadOnlySpan<short> samples);

internal interface ITransmitAudioBackendPort
{
    IAudioBackend CreateAudioBackend();
    IVocoderBackend CreateVocoderBackend();
}

internal interface ITransmitKeyPort
{
    IP25KeyResolver? P25 { get; }
    IDmrKeyResolver? Dmr { get; }
    INxdnKeyResolver? Nxdn { get; }
}

internal interface ITransmitSampleObservationPort
{
    bool ObservesBorrowedSamples { get; }
    bool ObservesOwnedSamples { get; }
    void ObserveBorrowed(ChannelId channelId, uint streamId, uint sourceId, ReadOnlySpan<short> samples);
    void ObserveOwned(ChannelId channelId, uint streamId, uint sourceId, ReadOnlyMemory<short> samples);
    void ReportFault(Exception exception);
}

internal sealed class TransmitAudioBackendPort(
    Func<IAudioBackend> createAudioBackend,
    Func<IVocoderBackend> createVocoderBackend) : ITransmitAudioBackendPort
{
    public IAudioBackend CreateAudioBackend() => createAudioBackend();
    public IVocoderBackend CreateVocoderBackend() => createVocoderBackend();
}

internal sealed record TransmitKeyPort(
    IP25KeyResolver? P25,
    IDmrKeyResolver? Dmr,
    INxdnKeyResolver? Nxdn) : ITransmitKeyPort;

internal sealed class TransmitSampleObservationPort(
    Action<ChannelId, uint, uint, ReadOnlyMemory<short>>? owned = null,
    BorrowedTransmitSamplesObserver? borrowed = null,
    Action<Exception>? fault = null) : ITransmitSampleObservationPort
{
    public bool ObservesBorrowedSamples => borrowed is not null;
    public bool ObservesOwnedSamples => owned is not null;

    public void ObserveBorrowed(
        ChannelId channelId,
        uint streamId,
        uint sourceId,
        ReadOnlySpan<short> samples)
        => borrowed?.Invoke(channelId, streamId, sourceId, samples);

    public void ObserveOwned(
        ChannelId channelId,
        uint streamId,
        uint sourceId,
        ReadOnlyMemory<short> samples)
        => owned?.Invoke(channelId, streamId, sourceId, samples);

    public void ReportFault(Exception exception)
    {
        if (fault is not null)
            fault(exception);
        else
            System.Diagnostics.Trace.TraceError("Transmit sample observer failed: {0}", exception);
    }
}

public sealed record MicrophoneStartExpectation(bool StartsCold, bool? IsBluetooth)
{
    public bool RequiresReceiveTransitionGate => StartsCold && IsBluetooth != false;
}

public enum DefaultInputRefreshResult
{
    NotRequired,
    Refreshed,
    DeferredUntilIdle
}

public sealed class ActiveChannelsChangedEventArgs : EventArgs
{
    internal ActiveChannelsChangedEventArgs(IEnumerable<ChannelId> channelIds)
    {
        ChannelIds = Array.AsReadOnly(channelIds.ToArray());
    }

    public IReadOnlyList<ChannelId> ChannelIds { get; }
}

// Lazily owns explicit transmit calls. Direct PTT starts one target; global
// PTT may start several targets, all fed by one microphone capture stream.
public sealed class ChannelTransmitCoordinator : IAsyncDisposable
{
    private static TimeSpan MicrophoneReadyTimeout { get; } =
        TimeSpan.FromSeconds(8);
    private static TimeSpan MicrophonePostCueRecoveryTimeout { get; } =
        TimeSpan.FromSeconds(2);
    private readonly ITransmitKeyPort keyPort;
    private readonly ITransmitSampleObservationPort sampleObservation;
    private readonly ITransmitAudioBackendPort backendPort;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly AsyncDisposal disposal = new();
    private IAudioBackend? audioBackend;
    private IVocoderBackend? vocoderBackend;
    private SharedAudioCapture? sharedCapture;
    private SharedAudioCapture.Lease? warmCaptureLease;
    private bool? sharedCaptureIsBluetooth;
    private bool sharedCaptureFollowsSystemDefault;
    private bool refreshDefaultInputWhenIdle;
    private readonly List<ActiveTransmit> active = [];
    private ActiveTransmit[] activeSnapshot = [];
    private int lifecycleState;
    private volatile bool microphoneAudioSuppressed;
    private AudioInputProcessingOptions audioInputOptions;
    private readonly TimeSpan microphoneStaleAfter;
    private CancellationTokenSource? microphoneMonitorCancellation;
    private Task microphoneMonitor = Task.CompletedTask;
    private int microphoneReadinessConfirmed;

    public event EventHandler<Exception>? Faulted;
    public event EventHandler<ActiveChannelsChangedEventArgs>? ActiveChannelsChanged;
    public MicrophoneHealth MicrophoneHealth
    {
        get
        {
            SharedAudioCapture? capture = sharedCapture;
            if (capture is null)
                return StoppedMicrophoneHealth;
            try
            {
                return capture.Health;
            }
            catch (ObjectDisposedException)
            {
                // A health poll may race the final capture lease disposal.
                return StoppedMicrophoneHealth;
            }
        }
    }
    public bool IsMicrophoneAudioSuppressed => microphoneAudioSuppressed;
    public TransmitQueueHealth QueueHealth
    {
        get
        {
            ActiveTransmit[] snapshot = Volatile.Read(ref activeSnapshot);
            if (snapshot.Length == 0)
                return default;

            TransmitQueueHealth[] health = snapshot
                .Select(entry => entry.Session.QueueHealth)
                .ToArray();
            return new TransmitQueueHealth(
                health.Sum(entry => entry.Depth),
                health.Sum(entry => entry.PeakDepth),
                health.Max(entry => entry.OldestAge),
                health.Sum(entry => entry.Capacity));
        }
    }
    public ChannelId? ActiveChannel => Volatile.Read(ref activeSnapshot).FirstOrDefault()?.Channel.Id;
    public IReadOnlyList<ChannelId> ActiveChannels => Volatile.Read(ref activeSnapshot)
        .Select(entry => entry.Channel.Id)
        .ToArray();
    public uint ActiveStreamId => Volatile.Read(ref activeSnapshot).FirstOrDefault()?.StreamId ?? 0;
    public bool ActiveMicrophoneStartedCold { get; private set; }
    public bool? ActiveMicrophoneIsBluetooth { get; private set; }

    private static MicrophoneHealth StoppedMicrophoneHealth { get; } = new(
        MicrophoneHealthState.Stopped,
        0,
        null,
        null,
        null);

    public async Task<MicrophoneStartExpectation> InspectNextMicrophoneStartAsync(
        bool? selectedInputIsBluetooth = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposingOrDisposed();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposingOrDisposed();
            if (sharedCapture is not null)
            {
                MicrophoneHealth health = sharedCapture.Health;
                if (warmCaptureLease is not null &&
                    active.Count == 0 &&
                    health.State is MicrophoneHealthState.Stale or MicrophoneHealthState.Faulted)
                {
                    await RestartSharedCaptureCoreAsync().ConfigureAwait(false);
                    health = sharedCapture?.Health ?? health;
                }
                return new MicrophoneStartExpectation(
                    StartsCold: health.State != MicrophoneHealthState.Ready,
                    sharedCaptureIsBluetooth);
            }

            // Audio Settings already owns a current device catalog. Reuse its
            // transport-neutral classification instead of constructing a
            // second backend and enumerating every endpoint immediately before
            // StartAsync repeats the work to open the selected microphone.
            if (selectedInputIsBluetooth is bool cachedBluetooth)
            {
                return new MicrophoneStartExpectation(
                    StartsCold: true,
                    cachedBluetooth);
            }

            return await Task.Run(() =>
            {
                using IAudioBackend backend = backendPort.CreateAudioBackend();
                AudioDeviceSelection selection = AudioDeviceSelector.Select(
                    backend.EnumerateDevices(AudioDirection.Input),
                    AudioDirection.Input,
                    audioInputOptions.DeviceId);
                return new MicrophoneStartExpectation(
                    StartsCold: true,
                    selection.Device.IsBluetooth);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public ChannelTransmitCoordinator(
        IP25KeyResolver? p25KeyResolver = null,
        AudioInputProcessingOptions? audioInputOptions = null,
        Action<ChannelId, uint, uint, ReadOnlyMemory<short>>? samplesObserver = null,
        Func<IAudioBackend>? createAudioBackend = null,
        Func<IVocoderBackend>? createVocoderBackend = null,
        IDmrKeyResolver? dmrKeyResolver = null,
        INxdnKeyResolver? nxdnKeyResolver = null,
        TimeSpan? microphoneStaleAfter = null,
        TimeProvider? timeProvider = null,
        BorrowedTransmitSamplesObserver? borrowedSamplesObserver = null,
        Action<Exception>? samplesObserverFaultHandler = null)
        : this(
            new TransmitKeyPort(p25KeyResolver, dmrKeyResolver, nxdnKeyResolver),
            audioInputOptions,
            new TransmitAudioBackendPort(
                createAudioBackend ?? (() => throw new InvalidOperationException(
                    "An audio backend factory is required for transmit.")),
                createVocoderBackend ?? (() => throw new InvalidOperationException(
                    "A vocoder backend factory is required for digital transmit."))),
            new TransmitSampleObservationPort(
                samplesObserver,
                borrowedSamplesObserver,
                samplesObserverFaultHandler),
            microphoneStaleAfter,
            timeProvider)
    {
    }

    internal ChannelTransmitCoordinator(
        ITransmitKeyPort keyPort,
        AudioInputProcessingOptions? audioInputOptions,
        ITransmitAudioBackendPort backendPort,
        ITransmitSampleObservationPort sampleObservation,
        TimeSpan? microphoneStaleAfter = null,
        TimeProvider? timeProvider = null)
    {
        this.keyPort = keyPort ?? throw new ArgumentNullException(nameof(keyPort));
        this.audioInputOptions = (audioInputOptions ?? new AudioInputProcessingOptions()).Normalize();
        this.microphoneStaleAfter = microphoneStaleAfter ?? TimeSpan.FromMilliseconds(250);
        if (this.microphoneStaleAfter <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(microphoneStaleAfter));
        this.backendPort = backendPort ?? throw new ArgumentNullException(nameof(backendPort));
        this.sampleObservation = sampleObservation ?? throw new ArgumentNullException(nameof(sampleObservation));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void UpdateAudioInputOptions(AudioInputProcessingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        audioInputOptions = options.Normalize();
    }

    // PTT startup may need the capture and call paths running before operator
    // audio is allowed onto the channel. Captured frames are discarded while
    // suppressed; they are never buffered and replayed later.
    public void SetMicrophoneAudioSuppressed(bool suppressed)
    {
        ThrowIfDisposingOrDisposed();
        microphoneAudioSuppressed = suppressed;
        sharedCapture?.SetSamplesSuppressed(suppressed);
    }

    // Keeps the selected capture device active between calls. This is useful
    // for Bluetooth headsets, whose microphone profile can take time to wake.
    public async Task SetKeepMicrophoneWarmAsync(bool enabled)
    {
        ThrowIfDisposingOrDisposed();
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposingOrDisposed();
            if (!enabled)
            {
                refreshDefaultInputWhenIdle = false;
                if (warmCaptureLease is not null)
                {
                    await warmCaptureLease.DisposeAsync().ConfigureAwait(false);
                    warmCaptureLease = null;
                }
                if (active.Count == 0)
                    await StopInfrastructureCoreAsync().ConfigureAwait(false);
                return;
            }

            if (warmCaptureLease is not null)
                return;

            await StartWarmCaptureCoreAsync().ConfigureAwait(false);
        }
        catch
        {
            if (active.Count == 0 && warmCaptureLease is null)
                await StopInfrastructureCoreAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    // Rebuilds a capture that is following the system-default microphone. An
    // active PTT call is never interrupted; warm capture is refreshed as soon
    // as that call ends. Fixed-device capture is left untouched.
    public async Task<DefaultInputRefreshResult> RefreshSystemDefaultInputAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposingOrDisposed();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposingOrDisposed();
            if (sharedCapture is null || !sharedCaptureFollowsSystemDefault)
                return DefaultInputRefreshResult.NotRequired;
            if (active.Count > 0)
            {
                refreshDefaultInputWhenIdle = true;
                return DefaultInputRefreshResult.DeferredUntilIdle;
            }

            await RestartSharedCaptureCoreAsync().ConfigureAwait(false);
            return DefaultInputRefreshResult.Refreshed;
        }
        finally
        {
            gate.Release();
        }
    }

    public uint GetActiveStreamId(ChannelId channelId)
    {
        return Volatile.Read(ref activeSnapshot)
            .FirstOrDefault(entry => entry.Channel.Id == channelId)?.StreamId ?? 0;
    }

    public async Task<MicrophoneReadinessTiming> WaitForMicrophoneReadyAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposingOrDisposed();
        SharedAudioCapture capture = sharedCapture ??
            throw new InvalidOperationException("The transmit microphone path has not been started.");
        if (Volatile.Read(ref activeSnapshot).Length == 0)
            throw new InvalidOperationException("No transmit call is waiting for microphone audio.");

        MicrophoneReadinessTiming timing = await capture.WaitForSamplesAsync(
            timeout ?? MicrophoneReadyTimeout,
            cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref microphoneReadinessConfirmed, 1);
        return timing;
    }

    // Releases the startup gate only after the selected microphone has proven
    // it delivered a fresh callback after permit-tone presentation or a cold
    // Bluetooth route transition. The proving callback remains suppressed.
    // Once the gate opens, the normal active-transmit stale/fault watchdog is
    // responsible for failing the call closed.
    public async Task<TimeSpan> ReleaseMicrophoneAudioAsync(
        bool requireFreshRecoveryCallback,
        TimeSpan? recoveryTimeout = null,
        TimeSpan postCueSuppressionDuration = default,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposingOrDisposed();
        SharedAudioCapture capture = sharedCapture ??
            throw new InvalidOperationException("The transmit microphone path has not been started.");
        if (Volatile.Read(ref activeSnapshot).Length == 0)
            throw new InvalidOperationException("No transmit call is waiting for microphone audio.");

        TimeSpan recovery = TimeSpan.Zero;
        if (requireFreshRecoveryCallback)
        {
            recovery = await capture.WaitForNextPhysicalSamplesAsync(
                recoveryTimeout ?? MicrophonePostCueRecoveryTimeout,
                postCueSuppressionDuration,
                cancellationToken).ConfigureAwait(false);
        }

        MicrophoneHealth health = capture.Health;
        if (health.State != MicrophoneHealthState.Ready)
        {
            string detail = string.IsNullOrWhiteSpace(health.Fault)
                ? health.State == MicrophoneHealthState.Stale
                    ? $"no fresh samples for {health.LastSampleAge?.TotalMilliseconds:0} ms"
                    : "capture path is not ready"
                : health.Fault;
            throw new IOException(
                $"Transmit microphone cannot be released while {health.State.ToString().ToLowerInvariant()}: {detail}.");
        }

        SetMicrophoneAudioSuppressed(false);
        return recovery;
    }

    public Task StartAsync(TransmitChannelDescriptor channel, IRadioTrafficEndpoint system)
        => StartAsync([new TransmitTarget(channel, system)]);

    // Activates every prepared protocol call as one coordinated transition.
    // Capture may be prepared well before this point while Bluetooth routes
    // settle, but no call-start packet is emitted until activation.
    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposingOrDisposed();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposingOrDisposed();
            if (active.Count == 0)
                throw new InvalidOperationException("No transmit call is prepared for activation.");

            foreach (ActiveTransmit entry in active)
                entry.Session.Activate();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StartAsync(IEnumerable<TransmitTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ThrowIfDisposingOrDisposed();
        TransmitTarget[] requested = targets
            .Where(target => target.Channel is not null && target.System is not null)
            .GroupBy(target => target.Channel.Id)
            .Select(group => group.First())
            .ToArray();
        if (requested.Length == 0)
            throw new InvalidOperationException("Select at least one transmit-capable channel.");

        var stateChanges = new List<ActiveChannelsChangedEventArgs>(2);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposingOrDisposed();
            ValidateTargets(requested);
            await StopCoreAsync(
                clearMicrophoneSuppression: false,
                forceDispose: false,
                stateChanges.Add).ConfigureAwait(false);

            IAudioBackend? createdAudioBackend = null;
            IVocoderBackend? createdVocoderBackend = null;
            SharedAudioCapture? createdSharedCapture = null;
            bool reusedWarmCapture = sharedCapture is not null;
            bool reusedReadyCapture = sharedCapture?.IsReady == true;
            var created = new List<ActiveTransmit>();
            try
            {
                if (sharedCapture is null)
                {
                    IAudioBackend backend = audioBackend ?? backendPort.CreateAudioBackend();
                    createdAudioBackend = backend;
                    createdSharedCapture = CreateSharedCapture(backend);
                }
                else
                    createdSharedCapture = sharedCapture;

                if (requested.Any(target => ChannelProtocolMediaMapper.RequiresVocoder(
                        target.Channel.Definition.Protocol)))
                    createdVocoderBackend = backendPort.CreateVocoderBackend();

                foreach (TransmitTarget target in requested)
                {
                    ChannelProtocol protocol = target.Channel.Definition.Protocol;
                    uint sourceId = target.System.SourceId!.Value;
                    uint streamId = target.System.CreateStreamId();
                    var dmrPrivacy = protocol == ChannelProtocol.Dmr ? CreateDmrPrivacyOptions(target.Channel) : null;
                    var nxdnPrivacy = protocol == ChannelProtocol.Nxdn ? CreateNxdnPrivacyOptions(target.Channel) : null;
                    var p25Encryption = protocol == ChannelProtocol.P25 ? CreateP25EncryptionOptions(target.Channel) : null;
                    SharedAudioCapture.Lease lease = createdSharedCapture!.CreateLease();
                    IVocoderSession? ownedVocoder = null;
                    try
                    {
                        Action<ReadOnlyMemory<byte>, ushort, uint> send = (payload, sequence, stream) => target.System.SendTraffic(
                            ChannelProtocolMediaMapper.ToTrafficProtocol(protocol),
                            payload,
                            sequence,
                            stream);

                        ITransmitCaptureSession session;
                        if (protocol == ChannelProtocol.Analog)
                        {
                            session = new AnalogTransmitCaptureSession(
                                lease,
                                sourceId,
                                target.Channel.Definition.DestinationId,
                                streamId,
                                send);
                        }
                        else
                        {
                            IVocoderSession vocoder = ownedVocoder = createdVocoderBackend!.CreateSession(
                                ChannelProtocolMediaMapper.ToVocoderMode(protocol));
                            session = protocol switch
                            {
                                ChannelProtocol.Dmr => new DmrTransmitCaptureSession(
                                    lease,
                                    vocoder,
                                    sourceId,
                                    target.Channel.Definition.DestinationId,
                                    target.Channel.Definition.Slot,
                                    streamId,
                                    send,
                                    dmrPrivacy),
                                ChannelProtocol.Nxdn => new NxdnTransmitCaptureSession(
                                        lease,
                                        vocoder,
                                        sourceId,
                                        target.Channel.Definition.DestinationId,
                                        streamId,
                                        send,
                                        privacy: nxdnPrivacy),
                                ChannelProtocol.P25 => new P25TransmitCaptureSession(
                                    lease,
                                    vocoder,
                                    sourceId,
                                    target.Channel.Definition.DestinationId,
                                    streamId,
                                    send,
                                    p25Encryption),
                                _ => throw new InvalidOperationException(
                                    $"Unsupported transmit protocol '{protocol}'.")
                            };
                        }

                        session.Faulted += HandleSessionFaulted;
                        created.Add(new ActiveTransmit(target.Channel, streamId, sourceId, session));
                    }
                    catch
                    {
                        try { ownedVocoder?.Dispose(); }
                        finally { await lease.DisposeAsync().ConfigureAwait(false); }
                        throw;
                    }
                }

                foreach (ActiveTransmit entry in created)
                    await entry.Session.StartAsync().ConfigureAwait(false);

                audioBackend ??= createdAudioBackend;
                vocoderBackend = createdVocoderBackend;
                sharedCapture ??= createdSharedCapture;
                active.AddRange(created);
                if (CommitActiveSnapshot() is { } stateChange)
                    stateChanges.Add(stateChange);
                ActiveMicrophoneStartedCold = !reusedReadyCapture;
                ActiveMicrophoneIsBluetooth = sharedCaptureIsBluetooth;
                Volatile.Write(ref microphoneReadinessConfirmed, reusedReadyCapture ? 1 : 0);
                StartMicrophoneMonitor();
            }
            catch (Exception preparationFailure)
            {
                var failures = new List<Exception> { preparationFailure };
                try { await DisposeEntriesAsync(created).ConfigureAwait(false); }
                catch (Exception exception) { failures.Add(exception); }
                if (!reusedWarmCapture && createdSharedCapture is not null)
                {
                    try { await createdSharedCapture.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception exception) { failures.Add(exception); }
                }
                try { createdVocoderBackend?.Dispose(); }
                catch (Exception exception) { failures.Add(exception); }
                if (!reusedWarmCapture)
                {
                    if (ReferenceEquals(audioBackend, createdAudioBackend))
                        audioBackend = null;
                    try { createdAudioBackend?.Dispose(); }
                    catch (Exception exception) { failures.Add(exception); }
                }
                if (failures.Count > 1)
                    throw new AggregateException("Transmit preparation and rollback failed.", failures);
                throw;
            }
        }
        finally
        {
            gate.Release();
            NotifyActiveChannelsChanged(stateChanges);
        }
    }

    public async Task StopAsync()
    {
        ThrowIfDisposingOrDisposed();
        ActiveChannelsChangedEventArgs? stateChange = null;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposingOrDisposed();
            await StopCoreAsync(
                clearMicrophoneSuppression: true,
                forceDispose: false,
                change => stateChange = change).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
            if (stateChange is not null)
                NotifyActiveChannelsChanged([stateChange]);
        }
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.CompareExchange(ref lifecycleState, 1, 0);
        return disposal.RunAsync(DisposeCoreAsync);
    }

    private async Task DisposeCoreAsync()
    {
        ActiveChannelsChangedEventArgs? stateChange = null;
        var failures = new List<Exception>();
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (warmCaptureLease is not null)
            {
                try
                {
                    await warmCaptureLease.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
                finally
                {
                    warmCaptureLease = null;
                }
            }
            try
            {
                await StopCoreAsync(
                    clearMicrophoneSuppression: true,
                    forceDispose: true,
                    change => stateChange = change).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
            Volatile.Write(ref lifecycleState, 2);
        }
        finally
        {
            gate.Release();
            if (stateChange is not null)
                NotifyActiveChannelsChanged([stateChange]);
        }

        if (failures.Count > 0)
            throw new AggregateException("Transmit cleanup failed.", failures);
    }

    private void ThrowIfDisposingOrDisposed()
        => ObjectDisposedException.ThrowIf(
            Volatile.Read(ref lifecycleState) != 0,
            this);

    private static void ValidateTargets(IEnumerable<TransmitTarget> targets)
    {
        foreach (TransmitTarget target in targets)
        {
            TransmitTargetPolicy.ThrowIfUnavailable(target.Channel, target.System);
            if (target.Channel.ReceiveActive && !target.Channel.AllowsTransmitDuringReceive)
                throw new InvalidOperationException($"{target.Channel.Name} is currently receiving.");
            if (!target.System.ChannelIds.Contains(target.Channel.Id))
                throw new InvalidOperationException($"{target.Channel.Name} does not belong to FNE system '{target.System.Name}'.");
            if (!target.System.IsConnected)
                throw new InvalidOperationException($"The FNE system '{target.System.Name}' is not connected.");
            if (target.System.SourceId is not uint sourceId || sourceId == 0)
                throw new InvalidOperationException($"The FNE system '{target.System.Name}' has no valid transmit RID.");
            if (target.Channel.Definition.Protocol == ChannelProtocol.Nxdn &&
                (sourceId > ushort.MaxValue || target.Channel.Definition.DestinationId > ushort.MaxValue))
            {
                throw new InvalidOperationException("NXDN transmit requires 16-bit source and destination IDs.");
            }
        }
    }

    private async Task StopCoreAsync(
        bool clearMicrophoneSuppression,
        bool forceDispose,
        Action<ActiveChannelsChangedEventArgs> stateChanged)
    {
        // Release microphone delivery for the whole selection before awaiting
        // any target. A slow terminator must not keep feeding the other calls.
        bool suppressionBeforeStop = microphoneAudioSuppressed;
        microphoneAudioSuppressed = true;
        sharedCapture?.SetSamplesSuppressed(true);
        await StopMicrophoneMonitorAsync().ConfigureAwait(false);
        ActiveTransmit[] current = active.AsEnumerable().Reverse().ToArray();
        Task<Exception?>[] stops = current
            .Select(entry => Task.Run(() => StopSessionAsync(entry)))
            .ToArray();
        Exception?[] stopFailures = await Task.WhenAll(stops).ConfigureAwait(false);
        var failures = new List<Exception>();
        for (int index = 0; index < current.Length; index++)
        {
            ActiveTransmit entry = current[index];
            bool stopConfirmed = stopFailures[index] is null;
            if (stopFailures[index] is { } stopFailure)
                failures.Add(stopFailure);

            if (!stopConfirmed && !forceDispose)
                continue;

            try
            {
                await DisposeEntryAsync(entry).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(new InvalidOperationException(
                    $"Transmit session disposal failed for '{entry.Channel.Name}'.",
                    exception));
            }
            finally
            {
                // A confirmed stop, or forced final disposal, has severed the
                // local capture/call path. Only now may ownership disappear.
                active.Remove(entry);
            }
        }

        if (CommitActiveSnapshot() is { } stateChange)
            stateChanged(stateChange);

        if (active.Count == 0)
        {
            Volatile.Write(ref microphoneReadinessConfirmed, 0);
            ActiveMicrophoneStartedCold = false;
            ActiveMicrophoneIsBluetooth = null;
        }

        // Keep startup frames gated until every transmit session has stopped.
        // Clearing suppression first creates a window where a failed permit
        // cue can leak microphone audio before cleanup sends terminators.
        if (active.Count == 0)
        {
            microphoneAudioSuppressed = !clearMicrophoneSuppression && suppressionBeforeStop;
            sharedCapture?.SetSamplesSuppressed(microphoneAudioSuppressed);
        }

        if (active.Count == 0 && sharedCapture is not null && warmCaptureLease is null)
        {
            try
            {
                await sharedCapture.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
            sharedCapture = null;
            sharedCaptureFollowsSystemDefault = false;
            sharedCaptureIsBluetooth = null;
        }
        if (active.Count == 0)
        {
            try
            {
                vocoderBackend?.Dispose();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
            vocoderBackend = null;
            if (warmCaptureLease is null)
            {
                try
                {
                    audioBackend?.Dispose();
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
                audioBackend = null;
                refreshDefaultInputWhenIdle = false;
            }
            else if (refreshDefaultInputWhenIdle)
            {
                refreshDefaultInputWhenIdle = false;
                try
                {
                    await RestartSharedCaptureCoreAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }
        }
        if (failures.Count == 1)
            throw failures[0];
        if (failures.Count > 1)
            throw new AggregateException("Transmit cleanup failed.", failures);
    }

    private async Task StopInfrastructureCoreAsync()
    {
        if (active.Count > 0 || warmCaptureLease is not null)
            throw new InvalidOperationException("Transmit audio infrastructure still has an active capture lease.");

        Exception? failure = null;
        if (sharedCapture is not null)
        {
            try
            {
                await sharedCapture.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            sharedCapture = null;
            sharedCaptureFollowsSystemDefault = false;
        }
        sharedCaptureIsBluetooth = null;

        try
        {
            audioBackend?.Dispose();
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }
        audioBackend = null;
        refreshDefaultInputWhenIdle = false;

        if (failure is not null)
            throw failure;
    }

    private static async Task<Exception?> StopSessionAsync(ActiveTransmit entry)
    {
        try
        {
            // Each target has its own capture lease and call drain. Dispatch
            // independently so even a synchronous transport stall cannot delay
            // requesting stop on the remaining targets.
            await entry.Session.StopAsync(CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return new InvalidOperationException(
                $"Transmit stop remains unconfirmed for '{entry.Channel.Name}'.",
                exception);
        }
    }

    private async Task DisposeEntriesAsync(IEnumerable<ActiveTransmit> entries)
    {
        Task[] disposals = entries
            .Reverse()
            .Select(DisposeEntryAsync)
            .ToArray();
        await Task.WhenAll(disposals).ConfigureAwait(false);
    }

    private async Task DisposeEntryAsync(ActiveTransmit entry)
    {
        entry.Session.Faulted -= HandleSessionFaulted;
        await entry.Session.DisposeAsync().ConfigureAwait(false);
    }

    private void HandleSessionFaulted(object? sender, Exception exception) => ReportFault(exception);

    private void StartMicrophoneMonitor()
    {
        CancellationTokenSource cancellation = new();
        microphoneMonitorCancellation = cancellation;
        microphoneMonitor = MonitorMicrophoneAsync(cancellation.Token);
    }

    private async Task StopMicrophoneMonitorAsync()
    {
        CancellationTokenSource? cancellation = microphoneMonitorCancellation;
        Task monitor = microphoneMonitor;
        microphoneMonitorCancellation = null;
        microphoneMonitor = Task.CompletedTask;
        if (cancellation is null)
            return;

        cancellation.Cancel();
        try
        {
            await monitor.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task MonitorMicrophoneAsync(CancellationToken cancellationToken)
    {
        bool observedReady = Volatile.Read(ref microphoneReadinessConfirmed) != 0;
        TimeSpan interval = TimeSpan.FromMilliseconds(Math.Clamp(
            microphoneStaleAfter.TotalMilliseconds / 2,
            10,
            250));
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(interval, timeProvider, cancellationToken).ConfigureAwait(false);
            MicrophoneHealth health = MicrophoneHealth;
            if (health.State == MicrophoneHealthState.Ready)
            {
                observedReady = true;
                Volatile.Write(ref microphoneReadinessConfirmed, 1);
                continue;
            }
            observedReady |= Volatile.Read(ref microphoneReadinessConfirmed) != 0;
            if (microphoneAudioSuppressed)
            {
                // Startup deliberately keeps operator audio gated while a
                // cold Bluetooth route reopens and warms the duplex output for
                // the talk-permit cue. CoreAudio can pause capture callbacks
                // during that transition. There is no microphone audio to
                // protect until the gate is released, so do not tear down the
                // shared session (and the cue with it) for that expected gap.
                continue;
            }
            if (!observedReady ||
                health.State is not (MicrophoneHealthState.Stale or MicrophoneHealthState.Faulted))
            {
                continue;
            }

            // Stop publishing capture callbacks before notifying the owner.
            // The owner then tears down all active calls and their UI state.
            SetMicrophoneAudioSuppressed(true);
            string detail = string.IsNullOrWhiteSpace(health.Fault)
                ? health.State == MicrophoneHealthState.Stale
                    ? $"no fresh samples for {health.LastSampleAge?.TotalMilliseconds:0} ms"
                    : "capture pump faulted"
                : health.Fault;
            ReportFault(new IOException(
                $"Transmit microphone became {health.State.ToString().ToLowerInvariant()}: {detail}."));
            return;
        }
    }

    private async Task StartWarmCaptureCoreAsync()
    {
        audioBackend ??= backendPort.CreateAudioBackend();
        sharedCapture ??= CreateSharedCapture(audioBackend);
        SharedAudioCapture.Lease lease = sharedCapture.CreateLease();
        try
        {
            await lease.StartAsync().ConfigureAwait(false);
            warmCaptureLease = lease;
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task RestartSharedCaptureCoreAsync()
    {
        if (active.Count > 0)
            throw new InvalidOperationException("Transmit audio is still active.");

        bool keepMicrophoneWarm = warmCaptureLease is not null;
        if (warmCaptureLease is not null)
        {
            await warmCaptureLease.DisposeAsync().ConfigureAwait(false);
            warmCaptureLease = null;
        }
        await StopInfrastructureCoreAsync().ConfigureAwait(false);
        if (!keepMicrophoneWarm)
            return;

        try
        {
            await StartWarmCaptureCoreAsync().ConfigureAwait(false);
        }
        catch
        {
            await StopInfrastructureCoreAsync().ConfigureAwait(false);
            throw;
        }
    }

    private SharedAudioCapture CreateSharedCapture(IAudioBackend backend)
    {
        AudioDeviceSelection selection = AudioDeviceSelector.Select(
            backend.EnumerateDevices(AudioDirection.Input),
            AudioDirection.Input,
            audioInputOptions.DeviceId);
        AudioDeviceInfo input = selection.Device;
        var capture = new ProcessedAudioCapture(
            backend.OpenCapture(input, PcmAudioFormat.Voice8KhzMono16Bit),
            audioInputOptions);
        if (sampleObservation.ObservesBorrowedSamples)
        {
            capture.BorrowedSamplesAvailable += samples =>
            {
                if (microphoneAudioSuppressed)
                    return;
                foreach (ActiveTransmit entry in Volatile.Read(ref activeSnapshot))
                {
                    try
                    {
                        sampleObservation.ObserveBorrowed(
                            entry.Channel.Id,
                            entry.StreamId,
                            entry.SourceId,
                            samples);
                    }
                    catch (Exception exception)
                    {
                        ReportSamplesObserverFailure(entry, exception);
                    }
                }
            };
        }
        else if (sampleObservation.ObservesOwnedSamples)
        {
            capture.SamplesAvailable += (_, args) =>
            {
                if (microphoneAudioSuppressed)
                    return;
                foreach (ActiveTransmit entry in Volatile.Read(ref activeSnapshot))
                {
                    try
                    {
                        sampleObservation.ObserveOwned(
                            entry.Channel.Id,
                            entry.StreamId,
                            entry.SourceId,
                            args.Samples);
                    }
                    catch (Exception exception)
                    {
                        ReportSamplesObserverFailure(entry, exception);
                    }
                }
            };
        }
        var shared = new SharedAudioCapture(
            capture,
            microphoneStaleAfter,
            timeProvider);
        shared.SetSamplesSuppressed(microphoneAudioSuppressed);
        sharedCaptureFollowsSystemDefault = selection.FollowsSystemDefault;
        sharedCaptureIsBluetooth = input.IsBluetooth;
        return shared;
    }

    private void ReportSamplesObserverFailure(ActiveTransmit entry, Exception exception)
    {
        var failure = new InvalidOperationException(
            $"Transmit sample observation failed for channel '{entry.Channel.Name}' " +
            $"and stream {entry.StreamId}.",
            exception);
        try
        {
            sampleObservation.ReportFault(failure);
        }
        catch (Exception reportingFailure)
        {
            System.Diagnostics.Trace.TraceError(
                "Transmit sample observer fault reporting failed: {0}; original failure: {1}",
                reportingFailure,
                failure);
        }
    }

    private P25TxEncryptionOptions? CreateP25EncryptionOptions(TransmitChannelDescriptor channel)
        => ChannelTransmitDefinitionFactory.CreateEncryptionOptions(
            channel,
            ChannelTransmitDefinitionFactory.Create(channel),
            keyPort.P25);

    private DmrPrivacyOptions? CreateDmrPrivacyOptions(TransmitChannelDescriptor channel)
        => ChannelTransmitDefinitionFactory.CreateDmrPrivacyOptions(
            channel,
            ChannelTransmitDefinitionFactory.Create(channel),
            keyPort.Dmr);

    private NxdnPrivacyOptions? CreateNxdnPrivacyOptions(TransmitChannelDescriptor channel)
        => ChannelTransmitDefinitionFactory.CreateNxdnPrivacyOptions(
            channel,
            ChannelTransmitDefinitionFactory.Create(channel),
            keyPort.Nxdn);

    // The coordinator gate remains the sole writer. Readers include capture
    // callbacks and UI properties, so publish an immutable point-in-time view
    // instead of enumerating the mutable lifecycle list concurrently.
    private ActiveChannelsChangedEventArgs? CommitActiveSnapshot()
    {
        ActiveTransmit[] previous = Volatile.Read(ref activeSnapshot);
        ActiveTransmit[] snapshot = active.ToArray();
        Volatile.Write(ref activeSnapshot, snapshot);
        if (previous.Length == snapshot.Length && previous
            .Select(entry => entry.Channel.Id)
            .SequenceEqual(snapshot.Select(entry => entry.Channel.Id)))
        {
            return null;
        }
        return new ActiveChannelsChangedEventArgs(snapshot.Select(entry => entry.Channel.Id));
    }

    private void NotifyActiveChannelsChanged(
        IEnumerable<ActiveChannelsChangedEventArgs> stateChanges)
    {
        foreach (ActiveChannelsChangedEventArgs stateChange in stateChanges)
        {
            EventHandler<ActiveChannelsChangedEventArgs>? observers = ActiveChannelsChanged;
            if (observers is null)
                continue;
            foreach (EventHandler<ActiveChannelsChangedEventArgs> observer in observers.GetInvocationList())
            {
                try
                {
                    observer(this, stateChange);
                }
                catch (Exception exception)
                {
                    ReportFault(new InvalidOperationException(
                        "An active-channel state observer failed.",
                        exception));
                }
            }
        }
    }

    private void ReportFault(Exception exception)
    {
        EventHandler<Exception>? observers = Faulted;
        if (observers is null)
        {
            System.Diagnostics.Trace.TraceError("Transmit coordinator fault: {0}", exception);
            return;
        }

        foreach (EventHandler<Exception> observer in observers.GetInvocationList())
        {
            try
            {
                observer(this, exception);
            }
            catch (Exception observerException)
            {
                System.Diagnostics.Trace.TraceError(
                    "Transmit fault observer failed: {0}",
                    observerException);
            }
        }
    }

    private sealed record ActiveTransmit(
        TransmitChannelDescriptor Channel,
        uint StreamId,
        uint SourceId,
        ITransmitCaptureSession Session);
}
