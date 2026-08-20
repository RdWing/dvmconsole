using System.Collections.Concurrent;
using DvmConsole.Audio;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Vocoder;

namespace DvmConsole.Desktop;

// Owns explicitly selected receive-audio channels. DMR/P25/NXDN/analog sessions
// share one output stream through a fixed-rate PCM mixer, and the coordinator
// serializes traffic processing within each channel while allowing different
// channels to decode concurrently before mixing.
// Audio devices and the vocoder are created only when Listen is used.
public sealed class ChannelReceiveAudioCoordinator : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim recoveryGate = new(1, 1);
    private readonly ConcurrentDictionary<ChannelViewModel, SessionState> sessions = [];
    private readonly Dictionary<ChannelViewModel, string> sessionRoutes = [];
    private readonly Dictionary<ChannelViewModel, bool> sessionFollowsSystemDefault = [];
    private readonly Dictionary<string, AudioRoute> audioRoutes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<IAudioBackend> createAudioBackend;
    private readonly Func<IVocoderBackend> createVocoderBackend;
    private readonly IP25KeyResolver? p25KeyResolver;
    private readonly IDmrKeyResolver? dmrKeyResolver;
    private readonly INxdnKeyResolver? nxdnKeyResolver;
    private readonly Action<ChannelViewModel, uint, uint, ReadOnlyMemory<short>>? samplesObserver;
    private readonly Func<ChannelViewModel, double>? getChannelGain;
    private readonly Func<ChannelViewModel, double>? getChannelBalance;
    private readonly Func<ChannelViewModel, string?>? getOutputDeviceId;
    private volatile ChannelViewModel[] activeChannels = [];
    private IVocoderBackend? vocoderBackend;
    private bool disposed;

    public ChannelReceiveAudioCoordinator()
        : this((IP25KeyResolver?)null)
    {
    }

    public ChannelReceiveAudioCoordinator(IP25KeyResolver? p25KeyResolver)
        : this(p25KeyResolver, null)
    {
    }

    public ChannelReceiveAudioCoordinator(
        IP25KeyResolver? p25KeyResolver,
        Action<ChannelViewModel, uint, uint, ReadOnlyMemory<short>>? samplesObserver,
        Func<ChannelViewModel, double>? getChannelGain = null,
        Func<ChannelViewModel, string?>? getOutputDeviceId = null,
        IDmrKeyResolver? dmrKeyResolver = null,
        INxdnKeyResolver? nxdnKeyResolver = null,
        Func<ChannelViewModel, double>? getChannelBalance = null)
        : this(
            () => AudioBackendFactory.CreateDefault(Environment.GetEnvironmentVariable("DVM_AUDIO_LIBRARY")),
            () => new SoftwareVocoderBackend(),
            p25KeyResolver,
            samplesObserver,
            getChannelGain,
            getOutputDeviceId,
            dmrKeyResolver,
            nxdnKeyResolver,
            getChannelBalance)
    {
    }

    public ChannelReceiveAudioCoordinator(
        Func<IAudioBackend> createAudioBackend,
        Func<IVocoderBackend> createVocoderBackend,
        IP25KeyResolver? p25KeyResolver = null,
        Action<ChannelViewModel, uint, uint, ReadOnlyMemory<short>>? samplesObserver = null,
        Func<ChannelViewModel, double>? getChannelGain = null,
        Func<ChannelViewModel, string?>? getOutputDeviceId = null,
        IDmrKeyResolver? dmrKeyResolver = null,
        INxdnKeyResolver? nxdnKeyResolver = null,
        Func<ChannelViewModel, double>? getChannelBalance = null)
    {
        this.createAudioBackend = createAudioBackend ?? throw new ArgumentNullException(nameof(createAudioBackend));
        this.createVocoderBackend = createVocoderBackend ?? throw new ArgumentNullException(nameof(createVocoderBackend));
        this.p25KeyResolver = p25KeyResolver;
        this.dmrKeyResolver = dmrKeyResolver;
        this.nxdnKeyResolver = nxdnKeyResolver;
        this.samplesObserver = samplesObserver;
        this.getChannelGain = getChannelGain;
        this.getChannelBalance = getChannelBalance;
        this.getOutputDeviceId = getOutputDeviceId;
    }

    public ChannelViewModel? ActiveChannel => activeChannels.FirstOrDefault();
    public IReadOnlyList<ChannelViewModel> ActiveChannels => activeChannels;

    public bool IsActive(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return Array.IndexOf(activeChannels, channel) >= 0;
    }

    public ReceiveAudioDiagnostics GetDiagnostics(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return sessions.TryGetValue(channel, out SessionState? state)
            ? state.Session.GetDiagnostics()
            : new ReceiveAudioDiagnostics(0, 0, 0, 0);
    }

    // Recreates the selected channel's audio route and receive session after
    // a platform playback device disappears. The bounded operation returns
    // false when the replacement device/backend cannot be opened, leaving the
    // channel stopped for an explicit operator retry.
    public async Task<bool> TryRecoverAsync(
        ChannelViewModel channel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!IsActive(channel))
            return false;
        ReceiveRouteRecoveryResult result = await RecoverSelectedAsync([channel], cancellationToken).ConfigureAwait(false);
        return result.Restarted.Contains(channel);
    }

    public async Task<ReceiveRouteRecoveryResult> RecoverSelectedAsync(
        IReadOnlyCollection<ChannelViewModel> desiredChannels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(desiredChannels);
        ChannelViewModel[] requested = desiredChannels
            .Where(channel => channel is not null)
            .Distinct()
            .ToArray();
        if (requested.Length == 0)
            return new ReceiveRouteRecoveryResult([], [], null);

        return await RestartChannelsAsync(
            () => ExpandSharedRouteSessions(requested),
            cancellationToken).ConfigureAwait(false);
    }

    // Re-resolves only sessions whose route policy follows the system default.
    // Fixed-device sessions remain on their selected endpoint, even when that
    // endpoint happened to be the old default.
    public Task<ReceiveRouteRecoveryResult> RefreshSystemDefaultOutputAsync(
        CancellationToken cancellationToken = default)
        => RestartChannelsAsync(SelectSystemDefaultSessions, cancellationToken);

    private async Task<ReceiveRouteRecoveryResult> RestartChannelsAsync(
        Func<ChannelViewModel[]> selectChannels,
        CancellationToken cancellationToken)
    {
        await recoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ChannelViewModel[] desired;
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                desired = selectChannels();
            }
            finally
            {
                gate.Release();
            }

            if (desired.Length == 0)
                return new ReceiveRouteRecoveryResult([], [], null);

            Exception? stopFailure = null;
            foreach (ChannelViewModel channel in desired)
            {
                try
                {
                    await StopAsync(channel, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    stopFailure ??= exception;
                }
            }

            var restarted = new List<ChannelViewModel>();
            var failed = new List<ChannelViewModel>();
            foreach (ChannelViewModel channel in desired)
            {
                try
                {
                    await StartAsync(channel, cancellationToken).ConfigureAwait(false);
                    if (IsActive(channel))
                        restarted.Add(channel);
                    else
                        failed.Add(channel);
                }
                catch
                {
                    failed.Add(channel);
                }
            }

            string? diagnostic = failed.Count > 0 || stopFailure is not null
                ? $"Restarted {restarted.Count} selected receive channel(s); {failed.Count} remain unavailable" +
                  (stopFailure is null ? "." : $" after cleanup reported: {stopFailure.Message}")
                : null;
            return new ReceiveRouteRecoveryResult(restarted, failed, diagnostic);
        }
        finally
        {
            recoveryGate.Release();
        }
    }

    // Called while gate is held.
    private ChannelViewModel[] SelectSystemDefaultSessions()
        => sessionFollowsSystemDefault
            .Where(pair => pair.Value && sessions.ContainsKey(pair.Key))
            .Select(pair => pair.Key)
            .ToArray();

    // Device-failure recovery must include every session that shares a failed
    // physical mixer route. Default-policy refresh does not use this expansion
    // because the old device is still healthy for fixed-route sessions.
    private ChannelViewModel[] ExpandSharedRouteSessions(ChannelViewModel[] requested)
    {
        var routeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ChannelViewModel channel in requested)
        {
            if (sessionRoutes.TryGetValue(channel, out string? routeId))
                routeIds.Add(routeId);
        }
        if (routeIds.Count == 0)
            return requested;

        return sessionRoutes
            .Where(pair => routeIds.Contains(pair.Value))
            .Select(pair => pair.Key)
            .Concat(requested)
            .Distinct()
            .ToArray();
    }

    public async Task StartAsync(ChannelViewModel channel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ObjectDisposedException.ThrowIf(disposed, this);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (sessions.ContainsKey(channel))
                return;
            if (channel.Definition.IsEncrypted &&
                !CanResolveEncryption(channel.Definition))
            {
                throw new NotSupportedException(
                    "Encrypted receive audio requires a configured key for this protocol; this channel cannot be opened safely.");
            }

            IAudioBackend? createdAudio = null;
            IVocoderBackend? createdVocoder = null;
            AudioRoute? createdRoute = null;
            IAudioPlayback? createdChannelPlayback = null;
            IVocoderSession? vocoderSession = null;
            ChannelReceiveAudioSession? nextSession = null;
            var sampleContext = new ReceiveSampleContext();

            try
            {
                IVocoderBackend? activeVocoder = channel.Definition.Mode is "dmr" or "p25" or "nxdn"
                    ? vocoderBackend ??= createdVocoder = createVocoderBackend()
                    : null;
                string? requestedDeviceId = getOutputDeviceId?.Invoke(channel);
                bool followsSystemDefault = false;
                AudioRoute? activeRoute = requestedDeviceId is not null &&
                    AudioDeviceSelector.HasSpecificRequest(requestedDeviceId) &&
                    audioRoutes.TryGetValue(requestedDeviceId, out AudioRoute? requestedRoute)
                    ? requestedRoute
                    : null;
                if (activeRoute is null)
                {
                    createdAudio = createAudioBackend();
                    activeRoute = GetOrCreateRoute(
                        createdAudio,
                        requestedDeviceId,
                        out createdRoute,
                        out followsSystemDefault);
                    if (createdRoute is null)
                    {
                        createdAudio.Dispose();
                        createdAudio = null;
                    }
                }
                IAudioPlayback mixerChannelPlayback = activeRoute.Mixer.OpenChannel();
                createdChannelPlayback = samplesObserver is null
                    ? mixerChannelPlayback
                    : new ObservedAudioPlayback(
                        mixerChannelPlayback,
                        samples =>
                        {
                            if (sampleContext.TryGet(out uint streamId, out uint sourceId))
                                samplesObserver(channel, streamId, sourceId, samples);
                        });

                if (activeVocoder is not null)
                {
                    VocoderMode mode = channel.Definition.Mode == "dmr"
                        ? VocoderMode.DmrAmbe
                        : channel.Definition.Mode == "nxdn"
                            ? VocoderMode.NxdnAmbe
                            : VocoderMode.P25Imbe;
                    vocoderSession = activeVocoder.CreateSession(mode);
                }
                nextSession = new ChannelReceiveAudioSession(
                    channel.Definition,
                    vocoderSession,
                    createdChannelPlayback,
                    p25KeyResolver,
                    dmrKeyResolver,
                    nxdnKeyResolver);
                nextSession.SetGain(getChannelGain?.Invoke(channel) ?? 1.0);
                nextSession.SetBalance(getChannelBalance?.Invoke(channel) ?? 0.0);
                vocoderSession = null;
                createdChannelPlayback = null;

                sessions.TryAdd(channel, new SessionState(nextSession, sampleContext));
                sessionRoutes.Add(channel, activeRoute.DeviceId);
                sessionFollowsSystemDefault.Add(channel, followsSystemDefault);
                activeChannels = sessions.Keys.ToArray();
                nextSession = null;
                createdAudio = null;
                createdVocoder = null;
                createdRoute = null;
            }
            catch
            {
                if (nextSession is not null)
                    await nextSession.DisposeAsync().ConfigureAwait(false);
                else
                {
                    if (createdChannelPlayback is not null)
                        await createdChannelPlayback.DisposeAsync().ConfigureAwait(false);
                    vocoderSession?.Dispose();
                }

                if (createdRoute is not null)
                {
                    audioRoutes.Remove(createdRoute.DeviceId);
                    await DisposeRouteAsync(createdRoute).ConfigureAwait(false);
                    createdAudio = null;
                }
                createdVocoder?.Dispose();
                createdAudio?.Dispose();
                if (createdVocoder is not null)
                    vocoderBackend = null;

                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private bool CanResolveEncryption(DvmConsole.Core.Runtime.ChannelRuntimeDefinition definition)
    {
        return definition.Mode switch
        {
            "p25" => p25KeyResolver?.CanResolve(
                definition.SystemName,
                definition.EncryptionAlgorithm,
                definition.EncryptionKeyId) == true,
            "dmr" => dmrKeyResolver?.CanResolve(
                definition.SystemName,
                definition.EncryptionAlgorithm,
                definition.EncryptionKeyId) == true,
            "nxdn" => nxdnKeyResolver?.CanResolve(
                definition.SystemName,
                definition.EncryptionAlgorithm,
                definition.EncryptionKeyId) == true,
            _ => false
        };
    }

    public async Task<int> ProcessAsync(
        ChannelViewModel channel,
        FneTrafficFrame traffic,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(traffic);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!sessions.TryGetValue(channel, out SessionState? state) || !state.TryAcquire())
            return 0;

        bool entered = false;
        try
        {
            await state.ProcessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            state.SampleContext.Set(traffic.StreamId, traffic.SourceId);
            try
            {
                return await state.Session.ProcessAsync(traffic, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                state.SampleContext.Clear();
            }
        }
        finally
        {
            if (entered)
                state.ProcessGate.Release();
            state.Release();
        }
    }

    public async Task StopAsync(ChannelViewModel channel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ObjectDisposedException.ThrowIf(disposed, this);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Exception? failure = null;
            if (sessions.TryRemove(channel, out SessionState? state))
            {
                state.BeginStop();
                sessionRoutes.Remove(channel, out string? routeId);
                sessionFollowsSystemDefault.Remove(channel);
                activeChannels = sessions.Keys.ToArray();
                try
                {
                    await state.WaitForIdleAsync().ConfigureAwait(false);
                    await state.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }

                if (routeId is not null && !sessionRoutes.Values.Contains(routeId, StringComparer.OrdinalIgnoreCase) &&
                    audioRoutes.Remove(routeId, out AudioRoute? route))
                {
                    try
                    {
                        await DisposeRouteAsync(route).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        failure ??= exception;
                    }
                }
            }

            if (sessions.Count == 0)
            {
                try
                {
                    await StopInfrastructureCoreAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }
            }

            if (failure is not null)
                throw failure;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SetGainAsync(ChannelViewModel channel, double gain, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ObjectDisposedException.ThrowIf(disposed, this);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (sessions.TryGetValue(channel, out SessionState? state))
                state.Session.SetGain(gain);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SetBalanceAsync(ChannelViewModel channel, double balance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ObjectDisposedException.ThrowIf(disposed, this);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (sessions.TryGetValue(channel, out SessionState? state))
                state.Session.SetBalance(balance);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
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
            if (!disposed)
            {
                await StopCoreAsync().ConfigureAwait(false);
                disposed = true;
            }
        }
        finally
        {
            gate.Release();
            gate.Dispose();
            recoveryGate.Dispose();
        }
    }

    private async Task StopCoreAsync()
    {
        SessionState[] oldSessions = sessions.Values.ToArray();
        sessions.Clear();
        sessionRoutes.Clear();
        sessionFollowsSystemDefault.Clear();
        activeChannels = [];

        foreach (SessionState state in oldSessions)
            state.BeginStop();

        Exception? failure = null;
        foreach (SessionState state in oldSessions)
        {
            try
            {
                await state.WaitForIdleAsync().ConfigureAwait(false);
                await state.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        try
        {
            await StopInfrastructureCoreAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        if (failure is not null)
            throw failure;
    }

    private async Task StopInfrastructureCoreAsync()
    {
        IVocoderBackend? oldVocoder = vocoderBackend;
        AudioRoute[] oldRoutes = audioRoutes.Values.ToArray();
        audioRoutes.Clear();
        vocoderBackend = null;

        Exception? failure = null;
        foreach (AudioRoute route in oldRoutes)
        {
            try
            {
                await DisposeRouteAsync(route).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }

        oldVocoder?.Dispose();

        if (failure is not null)
            throw failure;
    }

    private static async Task DisposeRouteAsync(AudioRoute route)
    {
        Exception? failure = null;
        try
        {
            await route.Mixer.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            route.Backend.Dispose();
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        if (failure is not null)
            throw failure;
    }

    private AudioRoute GetOrCreateRoute(
        IAudioBackend backend,
        string? requestedDeviceId,
        out AudioRoute? createdRoute,
        out bool followsSystemDefault)
    {
        AudioDeviceSelection selection = AudioDeviceSelector.Select(
            backend.EnumerateDevices(AudioDirection.Output),
            AudioDirection.Output,
            requestedDeviceId);
        AudioDeviceInfo output = selection.Device;
        followsSystemDefault = selection.FollowsSystemDefault;

        if (audioRoutes.TryGetValue(output.Id, out AudioRoute? existingRoute))
        {
            createdRoute = null;
            return existingRoute;
        }

        IAudioPlayback playback;
        try
        {
            playback = backend.OpenPlayback(output, PcmAudioFormat.Voice8KhzStereo16Bit);
        }
        catch (NotSupportedException)
        {
            playback = backend.OpenPlayback(output, PcmAudioFormat.Voice8KhzMono16Bit);
        }
        var route = new AudioRoute(output.Id, backend, new AudioMixer(playback));
        audioRoutes.Add(route.DeviceId, route);
        createdRoute = route;
        return route;
    }

    private sealed record AudioRoute(string DeviceId, IAudioBackend Backend, AudioMixer Mixer);

    private sealed class SessionState(
        ChannelReceiveAudioSession session,
        ReceiveSampleContext sampleContext) : IAsyncDisposable
    {
        private readonly object sync = new();
        private readonly TaskCompletionSource idle =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int operations;
        private bool stopping;

        public ChannelReceiveAudioSession Session { get; } = session;
        public ReceiveSampleContext SampleContext { get; } = sampleContext;
        public SemaphoreSlim ProcessGate { get; } = new(1, 1);

        public bool TryAcquire()
        {
            lock (sync)
            {
                if (stopping)
                    return false;

                operations++;
                return true;
            }
        }

        public void Release()
        {
            lock (sync)
            {
                operations--;
                if (stopping && operations == 0)
                    idle.TrySetResult();
            }
        }

        public void BeginStop()
        {
            lock (sync)
            {
                stopping = true;
                if (operations == 0)
                    idle.TrySetResult();
            }
        }

        public Task WaitForIdleAsync() => idle.Task;

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync().ConfigureAwait(false);
            ProcessGate.Dispose();
        }
    }

    private sealed class ReceiveSampleContext
    {
        private uint streamId;
        private uint sourceId;

        public void Set(uint nextStreamId, uint nextSourceId)
        {
            streamId = nextStreamId;
            sourceId = nextSourceId;
        }

        public void Clear()
        {
            streamId = 0;
            sourceId = 0;
        }

        public bool TryGet(out uint currentStreamId, out uint currentSourceId)
        {
            currentStreamId = streamId;
            currentSourceId = sourceId;
            return currentStreamId != 0 && currentSourceId != 0;
        }
    }

    private sealed class ObservedAudioPlayback(
        IAudioPlayback inner,
        Action<ReadOnlyMemory<short>> observer) : IAudioPlayback, IAudioGainControl, IAudioBalanceControl
    {
        public PcmAudioFormat Format => inner.Format;

        public double Gain
        {
            get => (inner as IAudioGainControl)?.Gain ?? 1.0;
            set
            {
                if (inner is IAudioGainControl gainControl)
                    gainControl.Gain = value;
            }
        }

        public double Balance
        {
            get => (inner as IAudioBalanceControl)?.Balance ?? 0.0;
            set
            {
                if (inner is IAudioBalanceControl balanceControl)
                    balanceControl.Balance = value;
            }
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(samples, cancellationToken).ConfigureAwait(false);
            observer(samples);
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default)
            => inner.FlushAsync(cancellationToken);

        public ValueTask DisposeAsync()
            => inner.DisposeAsync();
    }
}

public sealed record ReceiveRouteRecoveryResult(
    IReadOnlyList<ChannelViewModel> Restarted,
    IReadOnlyList<ChannelViewModel> Failed,
    string? Diagnostic);
