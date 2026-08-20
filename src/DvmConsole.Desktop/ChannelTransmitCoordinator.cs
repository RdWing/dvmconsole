using DvmConsole.Audio;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Vocoder;

namespace DvmConsole.Desktop;

public sealed record TransmitTarget(ChannelViewModel Channel, IFneTrafficEndpoint System);

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
    private bool sharedCaptureFollowsSystemDefault;
    private bool refreshDefaultInputWhenIdle;
    private readonly List<ActiveTransmit> active = [];
    private bool disposed;
    private volatile bool microphoneAudioSuppressed;
    private AudioInputProcessingOptions audioInputOptions;

    public event EventHandler<Exception>? Faulted;
    public event EventHandler<HighQualityBluetoothAudioStatus>? HighQualityBluetoothStatusChanged;
    public ChannelViewModel? ActiveChannel => active.FirstOrDefault()?.Channel;
    public IReadOnlyList<ChannelViewModel> ActiveChannels => active.Select(entry => entry.Channel).ToArray();
    public uint ActiveStreamId => active.FirstOrDefault()?.StreamId ?? 0;

    public ChannelTransmitCoordinator(
        IP25KeyResolver? p25KeyResolver = null,
        AudioInputProcessingOptions? audioInputOptions = null,
        Action<ChannelViewModel, uint, uint, ReadOnlyMemory<short>>? samplesObserver = null,
        Func<IAudioBackend>? createAudioBackend = null,
        Func<IVocoderBackend>? createVocoderBackend = null,
        IDmrKeyResolver? dmrKeyResolver = null,
        INxdnKeyResolver? nxdnKeyResolver = null)
    {
        this.p25KeyResolver = p25KeyResolver;
        this.dmrKeyResolver = dmrKeyResolver;
        this.nxdnKeyResolver = nxdnKeyResolver;
        this.audioInputOptions = (audioInputOptions ?? new AudioInputProcessingOptions()).Normalize();
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
        return active.FirstOrDefault(entry => ReferenceEquals(entry.Channel, channel))?.StreamId ?? 0;
    }

    public async Task WaitForMicrophoneReadyAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        SharedAudioCapture capture = sharedCapture ??
            throw new InvalidOperationException("The transmit microphone path has not been started.");
        if (active.Count == 0)
            throw new InvalidOperationException("No transmit call is waiting for microphone audio.");

        await capture.WaitForSamplesAsync(
            timeout ?? TimeSpan.FromSeconds(5),
            cancellationToken).ConfigureAwait(false);
    }

    public Task StartAsync(ChannelViewModel channel, IFneTrafficEndpoint system)
        => StartAsync([new TransmitTarget(channel, system)]);

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

                if (requested.Any(target => target.Channel.Definition.Mode != "analog"))
                    createdVocoderBackend = createVocoderBackend();

                foreach (TransmitTarget target in requested)
                {
                    bool isDmr = target.Channel.Definition.Mode == "dmr";
                    bool isNxdn = target.Channel.Definition.Mode == "nxdn";
                    bool isAnalog = target.Channel.Definition.Mode == "analog";
                    uint sourceId = target.System.SourceId!.Value;
                    uint streamId = target.System.CreateStreamId();
                    SharedAudioCapture.Lease lease = createdSharedCapture!.CreateLease();
                    Action<ReadOnlyMemory<byte>, ushort, uint> send = (payload, sequence, stream) => target.System.SendTraffic(
                        isDmr
                            ? FneTrafficProtocol.Dmr
                            : isNxdn
                                ? FneTrafficProtocol.Nxdn
                            : isAnalog
                                ? FneTrafficProtocol.Analog
                                : FneTrafficProtocol.P25,
                        payload.Span,
                        sequence,
                        stream);

                    ITransmitCaptureSession session;
                    if (isAnalog)
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
                            isDmr
                                ? VocoderMode.DmrAmbe
                                : isNxdn
                                    ? VocoderMode.NxdnAmbe
                                    : VocoderMode.P25Imbe);
                        session = isDmr
                            ? new DmrTransmitCaptureSession(
                                lease,
                                vocoder,
                                sourceId,
                                target.Channel.Definition.DestinationId,
                                target.Channel.Definition.Slot,
                                streamId,
                                send,
                                CreateDmrPrivacyOptions(target.Channel))
                            : isNxdn
                                ? new NxdnTransmitCaptureSession(
                                    lease,
                                    vocoder,
                                    sourceId,
                                    target.Channel.Definition.DestinationId,
                                    streamId,
                                    send,
                                    privacy: CreateNxdnPrivacyOptions(target.Channel))
                            : new P25TransmitCaptureSession(
                                lease,
                                vocoder,
                                sourceId,
                                target.Channel.Definition.DestinationId,
                                streamId,
                                send,
                                CreateP25EncryptionOptions(target.Channel));
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
            if (!target.Channel.CanTransmit)
                throw new InvalidOperationException($"{target.Channel.Name} is RX-only or cannot transmit with its configured encryption.");
            if (!target.System.Channels.Contains(target.Channel))
                throw new InvalidOperationException($"{target.Channel.Name} does not belong to FNE system '{target.System.Name}'.");
            // DMR transmission is intentionally fail-open with respect to the
            // master's announced talkgroup table. AnnouncedTGs is advisory for
            // this console; a missing/deactivated entry must not disable PTT.
            if (!target.System.IsConnected)
                throw new InvalidOperationException($"The FNE system '{target.System.Name}' is not connected.");
            if (target.System.SourceId is not uint sourceId || sourceId == 0)
                throw new InvalidOperationException($"The FNE system '{target.System.Name}' has no valid transmit RID.");
            if (target.Channel.Definition.Mode == "nxdn" &&
                (sourceId > ushort.MaxValue || target.Channel.Definition.DestinationId > ushort.MaxValue))
            {
                throw new InvalidOperationException("NXDN transmit requires 16-bit source and destination IDs.");
            }
        }
    }

    private async Task StopCoreAsync(bool clearMicrophoneSuppression)
    {
        ActiveTransmit[] current = active.ToArray();
        active.Clear();
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
        Exception? failure = null;
        foreach (ActiveTransmit entry in entries.Reverse())
        {
            entry.Session.Faulted -= HandleSessionFaulted;
            try
            {
                await entry.Session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }
        if (failure is not null)
            throw failure;
    }

    private void HandleSessionFaulted(object? sender, Exception exception) => Faulted?.Invoke(this, exception);

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
                foreach (ActiveTransmit entry in active)
                    samplesObserver(entry.Channel, entry.StreamId, entry.SourceId, args.Samples);
            };
        }
        var shared = new SharedAudioCapture(capture);
        shared.SetSamplesSuppressed(microphoneAudioSuppressed);
        sharedCaptureFollowsSystemDefault = selection.FollowsSystemDefault;
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

    private sealed record ActiveTransmit(
        ChannelViewModel Channel,
        uint StreamId,
        uint SourceId,
        ITransmitCaptureSession Session);
}
