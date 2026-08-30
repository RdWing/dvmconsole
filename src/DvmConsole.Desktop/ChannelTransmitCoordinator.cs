using DvmConsole.Audio;
using DvmConsole.Core.Runtime;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Operations;
using DvmConsole.Vocoder;

namespace DvmConsole.Desktop;

public sealed record TransmitTarget(ChannelViewModel Channel, IFneTrafficEndpoint System);

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

// Lazily owns explicit transmit calls. Direct PTT starts one target; global
// PTT may start several targets, all fed by one microphone capture stream.
public sealed class ChannelTransmitCoordinator : IAsyncDisposable
{
    private static TimeSpan MicrophoneReadyTimeout { get; } =
        TimeSpan.FromSeconds(8);
    private static TimeSpan MicrophonePostCueRecoveryTimeout { get; } =
        TimeSpan.FromSeconds(2);
    private readonly IP25KeyResolver? p25KeyResolver;
    private readonly IDmrKeyResolver? dmrKeyResolver;
    private readonly INxdnKeyResolver? nxdnKeyResolver;
    private readonly Action<ChannelViewModel, uint, uint, ReadOnlyMemory<short>>? samplesObserver;
    private readonly Func<IAudioBackend> createAudioBackend;
    private readonly Func<IVocoderBackend> createVocoderBackend;
    private readonly SemaphoreSlim gate = new(1, 1);
    private IAudioBackend? audioBackend;
    private IVocoderBackend? vocoderBackend;
    private SharedAudioCapture? sharedCapture;
    private SharedAudioCapture.Lease? warmCaptureLease;
    private bool? sharedCaptureIsBluetooth;
    private bool sharedCaptureFollowsSystemDefault;
    private bool refreshDefaultInputWhenIdle;
    private readonly List<ActiveTransmit> active = [];
    private ActiveTransmit[] activeSnapshot = [];
    private bool disposed;
    private volatile bool microphoneAudioSuppressed;
    private AudioInputProcessingOptions audioInputOptions;
    private readonly TimeSpan microphoneStaleAfter;
    private CancellationTokenSource? microphoneMonitorCancellation;
    private Task microphoneMonitor = Task.CompletedTask;
    private int microphoneReadinessConfirmed;

    public event EventHandler<Exception>? Faulted;
    public event EventHandler<HighQualityBluetoothAudioStatus>? HighQualityBluetoothStatusChanged;
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
    public ChannelViewModel? ActiveChannel => Volatile.Read(ref activeSnapshot).FirstOrDefault()?.Channel;
    public IReadOnlyList<ChannelViewModel> ActiveChannels => Volatile.Read(ref activeSnapshot)
        .Select(entry => entry.Channel)
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
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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

            return await Task.Run(() =>
            {
                using IAudioBackend backend = createAudioBackend();
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
        Action<ChannelViewModel, uint, uint, ReadOnlyMemory<short>>? samplesObserver = null,
        Func<IAudioBackend>? createAudioBackend = null,
        Func<IVocoderBackend>? createVocoderBackend = null,
        IDmrKeyResolver? dmrKeyResolver = null,
        INxdnKeyResolver? nxdnKeyResolver = null,
        TimeSpan? microphoneStaleAfter = null)
    {
        this.p25KeyResolver = p25KeyResolver;
        this.dmrKeyResolver = dmrKeyResolver;
        this.nxdnKeyResolver = nxdnKeyResolver;
        this.audioInputOptions = (audioInputOptions ?? new AudioInputProcessingOptions()).Normalize();
        this.microphoneStaleAfter = microphoneStaleAfter ?? TimeSpan.FromMilliseconds(250);
        if (this.microphoneStaleAfter <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(microphoneStaleAfter));
        this.samplesObserver = samplesObserver;
        this.createAudioBackend = createAudioBackend ??
            (() => AudioBackendFactory.CreateDefault(Environment.GetEnvironmentVariable("DVM_AUDIO_LIBRARY")));
        this.createVocoderBackend = createVocoderBackend ??
            (() => new SoftwareVocoderBackend());
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
        ObjectDisposedException.ThrowIf(disposed, this);
        microphoneAudioSuppressed = suppressed;
        sharedCapture?.SetSamplesSuppressed(suppressed);
    }

    // Keeps the selected capture device active between calls. This is useful
    // for Bluetooth headsets, whose microphone profile can take time to wake.
    public async Task SetKeepMicrophoneWarmAsync(bool enabled)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
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
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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

    public uint GetActiveStreamId(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return Volatile.Read(ref activeSnapshot)
            .FirstOrDefault(entry => ReferenceEquals(entry.Channel, channel))?.StreamId ?? 0;
    }

    public async Task<MicrophoneReadinessTiming> WaitForMicrophoneReadyAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
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
    // it resumed following a cold Bluetooth permit-tone route transition.
    // Once the gate opens, the normal active-transmit stale/fault watchdog is
    // responsible for failing the call closed.
    public async Task<TimeSpan> ReleaseMicrophoneAudioAsync(
        bool requireFreshRecoveryCallback,
        TimeSpan? recoveryTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        SharedAudioCapture capture = sharedCapture ??
            throw new InvalidOperationException("The transmit microphone path has not been started.");
        if (Volatile.Read(ref activeSnapshot).Length == 0)
            throw new InvalidOperationException("No transmit call is waiting for microphone audio.");

        TimeSpan recovery = TimeSpan.Zero;
        if (requireFreshRecoveryCallback)
        {
            recovery = await capture.WaitForNextPhysicalSamplesAsync(
                recoveryTimeout ?? MicrophonePostCueRecoveryTimeout,
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

    public Task StartAsync(ChannelViewModel channel, IFneTrafficEndpoint system)
        => StartAsync([new TransmitTarget(channel, system)]);

    // Activates every prepared protocol call as one coordinated transition.
    // Capture may be prepared well before this point while Bluetooth routes
    // settle, but no call-start packet is emitted until activation.
    public async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
        ObjectDisposedException.ThrowIf(disposed, this);
        TransmitTarget[] requested = targets
            .Where(target => target.Channel is not null && target.System is not null)
            .GroupBy(target => target.Channel)
            .Select(group => group.First())
            .ToArray();
        if (requested.Length == 0)
            throw new InvalidOperationException("Select at least one transmit-capable channel.");

        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ValidateTargets(requested);
            await StopCoreAsync(clearMicrophoneSuppression: false).ConfigureAwait(false);

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
                    IAudioBackend backend = audioBackend ??= createAudioBackend();
                    createdAudioBackend = backend;
                    createdSharedCapture = CreateSharedCapture(backend);
                }
                else
                    createdSharedCapture = sharedCapture;

                if (requested.Any(target => ChannelProtocolMediaMapper.RequiresVocoder(
                        target.Channel.Definition.Protocol)))
                    createdVocoderBackend = createVocoderBackend();

                foreach (TransmitTarget target in requested)
                {
                    ChannelProtocol protocol = target.Channel.Definition.Protocol;
                    uint sourceId = target.System.SourceId!.Value;
                    uint streamId = target.System.CreateStreamId();
                    SharedAudioCapture.Lease lease = createdSharedCapture!.CreateLease();
                    Action<ReadOnlyMemory<byte>, ushort, uint> send = (payload, sequence, stream) => target.System.SendTraffic(
                        ChannelProtocolMediaMapper.ToTrafficProtocol(protocol),
                        payload.Span,
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
                        IVocoderSession vocoder = createdVocoderBackend!.CreateSession(
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
                                CreateDmrPrivacyOptions(target.Channel)),
                            ChannelProtocol.Nxdn => new NxdnTransmitCaptureSession(
                                    lease,
                                    vocoder,
                                    sourceId,
                                    target.Channel.Definition.DestinationId,
                                    streamId,
                                    send,
                                    privacy: CreateNxdnPrivacyOptions(target.Channel)),
                            ChannelProtocol.P25 => new P25TransmitCaptureSession(
                                lease,
                                vocoder,
                                sourceId,
                                target.Channel.Definition.DestinationId,
                                streamId,
                                send,
                                CreateP25EncryptionOptions(target.Channel)),
                            _ => throw new InvalidOperationException(
                                $"Unsupported transmit protocol '{protocol}'.")
                        };
                    }

                    session.Faulted += HandleSessionFaulted;
                    created.Add(new ActiveTransmit(target.Channel, streamId, sourceId, session));
                }

                foreach (ActiveTransmit entry in created)
                    await entry.Session.StartAsync().ConfigureAwait(false);

                ReportHighQualityBluetoothStatus(createdAudioBackend ?? audioBackend);

                audioBackend ??= createdAudioBackend;
                vocoderBackend = createdVocoderBackend;
                sharedCapture ??= createdSharedCapture;
                active.AddRange(created);
                PublishActiveSnapshot();
                ActiveMicrophoneStartedCold = !reusedReadyCapture;
                ActiveMicrophoneIsBluetooth = sharedCaptureIsBluetooth;
                Volatile.Write(ref microphoneReadinessConfirmed, reusedReadyCapture ? 1 : 0);
                StartMicrophoneMonitor();
            }
            catch
            {
                await DisposeEntriesAsync(created).ConfigureAwait(false);
                if (!reusedWarmCapture && createdSharedCapture is not null)
                    await createdSharedCapture.DisposeAsync().ConfigureAwait(false);
                createdVocoderBackend?.Dispose();
                if (!reusedWarmCapture)
                    createdAudioBackend?.Dispose();
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private void ReportHighQualityBluetoothStatus(IAudioBackend? backend)
    {
        if (backend is IHighQualityBluetoothAudioStatus statusProvider)
            HighQualityBluetoothStatusChanged?.Invoke(this, statusProvider.HighQualityBluetoothStatus);
    }

    public async Task StopAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync(clearMicrophoneSuppression: true).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (warmCaptureLease is not null)
            {
                await warmCaptureLease.DisposeAsync().ConfigureAwait(false);
                warmCaptureLease = null;
            }
            await StopCoreAsync(clearMicrophoneSuppression: true).ConfigureAwait(false);
            disposed = true;
        }
        finally
        {
            gate.Release();
            gate.Dispose();
        }
    }

    private static void ValidateTargets(IEnumerable<TransmitTarget> targets)
    {
        foreach (TransmitTarget target in targets)
        {
            TransmitTargetPolicy.ThrowIfUnavailable(target.Channel, target.System);
            if (target.Channel.IsReceivePresentationActive)
                throw new InvalidOperationException($"{target.Channel.Name} is currently receiving.");
            if (!target.System.Channels.Contains(target.Channel))
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

    private async Task StopCoreAsync(bool clearMicrophoneSuppression)
    {
        await StopMicrophoneMonitorAsync().ConfigureAwait(false);
        ActiveTransmit[] current = active.ToArray();
        active.Clear();
        PublishActiveSnapshot();
        Volatile.Write(ref microphoneReadinessConfirmed, 0);
        ActiveMicrophoneStartedCold = false;
        ActiveMicrophoneIsBluetooth = null;
        Exception? failure = null;
        try
        {
            await DisposeEntriesAsync(current).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        // Keep startup frames gated until every transmit session has stopped.
        // Clearing suppression first creates a window where a failed permit
        // cue can leak microphone audio before cleanup sends terminators.
        if (clearMicrophoneSuppression)
        {
            microphoneAudioSuppressed = false;
            sharedCapture?.SetSamplesSuppressed(false);
        }

        if (sharedCapture is not null && warmCaptureLease is null)
        {
            try
            {
                await sharedCapture.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
            sharedCapture = null;
            sharedCaptureFollowsSystemDefault = false;
            sharedCaptureIsBluetooth = null;
        }
        vocoderBackend?.Dispose();
        vocoderBackend = null;
        if (warmCaptureLease is null)
        {
            audioBackend?.Dispose();
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
                failure ??= exception;
            }
        }
        if (failure is not null)
            throw failure;
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

    private void HandleSessionFaulted(object? sender, Exception exception) => Faulted?.Invoke(this, exception);

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
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
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
            Faulted?.Invoke(
                this,
                new IOException($"Transmit microphone became {health.State.ToString().ToLowerInvariant()}: {detail}."));
            return;
        }
    }

    private async Task StartWarmCaptureCoreAsync()
    {
        audioBackend ??= createAudioBackend();
        sharedCapture ??= CreateSharedCapture(audioBackend);
        SharedAudioCapture.Lease lease = sharedCapture.CreateLease();
        try
        {
            await lease.StartAsync().ConfigureAwait(false);
            warmCaptureLease = lease;
            ReportHighQualityBluetoothStatus(audioBackend);
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
        if (samplesObserver is not null)
        {
            capture.SamplesAvailable += (_, args) =>
            {
                if (microphoneAudioSuppressed)
                    return;
                foreach (ActiveTransmit entry in Volatile.Read(ref activeSnapshot))
                    samplesObserver(entry.Channel, entry.StreamId, entry.SourceId, args.Samples);
            };
        }
        var shared = new SharedAudioCapture(
            capture,
            microphoneStaleAfter);
        shared.SetSamplesSuppressed(microphoneAudioSuppressed);
        sharedCaptureFollowsSystemDefault = selection.FollowsSystemDefault;
        sharedCaptureIsBluetooth = input.IsBluetooth;
        return shared;
    }

    private P25TxEncryptionOptions? CreateP25EncryptionOptions(ChannelViewModel channel)
        => ChannelTransmitDefinitionFactory.CreateEncryptionOptions(
            channel,
            ChannelTransmitDefinitionFactory.Create(channel),
            p25KeyResolver);

    private DmrPrivacyOptions? CreateDmrPrivacyOptions(ChannelViewModel channel)
        => ChannelTransmitDefinitionFactory.CreateDmrPrivacyOptions(
            channel,
            ChannelTransmitDefinitionFactory.Create(channel),
            dmrKeyResolver);

    private NxdnPrivacyOptions? CreateNxdnPrivacyOptions(ChannelViewModel channel)
        => ChannelTransmitDefinitionFactory.CreateNxdnPrivacyOptions(
            channel,
            ChannelTransmitDefinitionFactory.Create(channel),
            nxdnKeyResolver);

    // The coordinator gate remains the sole writer. Readers include capture
    // callbacks and UI properties, so publish an immutable point-in-time view
    // instead of enumerating the mutable lifecycle list concurrently.
    private void PublishActiveSnapshot()
        => Volatile.Write(ref activeSnapshot, active.ToArray());

    private sealed record ActiveTransmit(
        ChannelViewModel Channel,
        uint StreamId,
        uint SourceId,
        ITransmitCaptureSession Session);
}
