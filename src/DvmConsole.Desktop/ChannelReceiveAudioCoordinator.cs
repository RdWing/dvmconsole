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
    private readonly object playbackPolicySync = new();
    private readonly ConcurrentDictionary<ChannelViewModel, ReceiveStreamSessionRegistry> sessions = [];
    private readonly ReceiveAudioRouteRegistry routeRegistry = new();
    private readonly Func<IAudioBackend> createAudioBackend;
    private readonly Func<IVocoderBackend> createVocoderBackend;
    private readonly Func<ChannelViewModel, double>? getChannelGain;
    private readonly Func<ChannelViewModel, double>? getChannelBalance;
    private readonly Func<ChannelViewModel, string?>? getOutputDeviceId;
    private readonly ReceiveSessionFactory receiveSessionFactory;
    private readonly Action<ChannelViewModel, uint, ReadOnlyMemory<short>, TimeSpan>?
        presentationSamplesObserver;
    private Func<ChannelViewModel, FneTrafficFrame, ReceivePlaybackEpisode> playbackEpisodeResolver =
        static (_, traffic) => new ReceivePlaybackEpisode(
            traffic.StreamId,
            traffic.StreamId,
            traffic.StreamId,
            RetainUntilEpisodeCompletion: false);
    private volatile ChannelViewModel[] activeChannels = [];
    private IVocoderBackend? vocoderBackend;
    private bool transitionPlaybackDiscarded;
    private bool operatorOutputMuted;
    private bool disposed;
    private TaskCompletionSource? disposeCompletion;

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
        Func<ChannelViewModel, double>? getChannelBalance = null,
        Action<ChannelViewModel, uint, ReadOnlyMemory<short>, TimeSpan>?
            presentationSamplesObserver = null)
        : this(
            () => AudioBackendFactory.CreateDefault(Environment.GetEnvironmentVariable("DVM_AUDIO_LIBRARY")),
            () => new SoftwareVocoderBackend(),
            p25KeyResolver,
            samplesObserver,
            getChannelGain,
            getOutputDeviceId,
            dmrKeyResolver,
            nxdnKeyResolver,
            getChannelBalance,
            presentationSamplesObserver)
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
        Func<ChannelViewModel, double>? getChannelBalance = null,
        Action<ChannelViewModel, uint, ReadOnlyMemory<short>, TimeSpan>?
            presentationSamplesObserver = null)
    {
        this.createAudioBackend = createAudioBackend ?? throw new ArgumentNullException(nameof(createAudioBackend));
        this.createVocoderBackend = createVocoderBackend ?? throw new ArgumentNullException(nameof(createVocoderBackend));
        this.getChannelGain = getChannelGain;
        this.getChannelBalance = getChannelBalance;
        this.getOutputDeviceId = getOutputDeviceId;
        this.presentationSamplesObserver = presentationSamplesObserver;
        receiveSessionFactory = new ReceiveSessionFactory(
            p25KeyResolver,
            dmrKeyResolver,
            nxdnKeyResolver,
            samplesObserver);
    }

    internal void SetReceivePlaybackEpisodeResolver(
        Func<ChannelViewModel, FneTrafficFrame, ReceivePlaybackEpisode> resolver)
        => playbackEpisodeResolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public ChannelViewModel? ActiveChannel => activeChannels.FirstOrDefault();
    public event Action<ReceiveAudioOutputFailure>? OutputFailed;
    public IReadOnlyList<ChannelViewModel> ActiveChannels => activeChannels;
    public IReadOnlyList<ChannelViewModel> LivePlaybackChannels => activeChannels
        .Where(IsLivePlaybackEnabled)
        .ToArray();

    public bool IsActive(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return Array.IndexOf(activeChannels, channel) >= 0;
    }

    public bool IsLivePlaybackEnabled(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return sessions.TryGetValue(channel, out ReceiveStreamSessionRegistry? state) &&
               state.LivePlaybackEnabled;
    }

    public bool IsTrackingStream(ChannelViewModel channel, uint streamId)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return streamId != 0 &&
               sessions.TryGetValue(channel, out ReceiveStreamSessionRegistry? state) &&
               state.IsTrackingStream(streamId);
    }

    public ReceiveAudioDiagnostics GetDiagnostics(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return sessions.TryGetValue(channel, out ReceiveStreamSessionRegistry? state)
            ? state.GetDiagnostics()
            : new ReceiveAudioDiagnostics(0, 0, 0, 0);
    }

    public AudioMixerDiagnostics? GetPlaybackDiagnostics(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return routeRegistry.TryGetRoute(channel, out ReceiveAudioRoute? route)
            ? route.Mixer.GetDiagnostics()
            : null;
    }

    internal EpisodeLivePlayoutDiagnostics GetPlaybackArbitrationDiagnostics(
        ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return sessions.TryGetValue(channel, out ReceiveStreamSessionRegistry? state)
            ? state.GetPlaybackArbitrationDiagnostics()
            : default;
    }

    public long SetLivePlaybackDiscarded(bool discarded)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (playbackPolicySync)
        {
            transitionPlaybackDiscarded = discarded;
            return ApplyOutputDiscardPolicyLocked();
        }
    }

    // Operator mute affects only live speaker-bound receive PCM. Decode,
    // lifecycle, patching, and TAR observation remain active upstream.
    public long SetOutputMuted(bool muted)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (playbackPolicySync)
        {
            operatorOutputMuted = muted;
            return ApplyOutputDiscardPolicyLocked();
        }
    }

    private long ApplyOutputDiscardPolicyLocked()
    {
        bool discarded = transitionPlaybackDiscarded || operatorOutputMuted;
        long totalDiscardedSamples = 0;
        foreach (ReceiveAudioRoute route in routeRegistry.RouteSnapshot)
        {
            try
            {
                totalDiscardedSamples = checked(
                    totalDiscardedSamples + route.Mixer.SetInputDiscarded(discarded));
            }
            catch (ObjectDisposedException)
            {
                // A route replacement may finish concurrently. A newly
                // created route applies the policy before it is published.
            }
        }
        return totalDiscardedSamples;
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
        ObjectDisposedException.ThrowIf(disposed, this);
        await recoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
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

            Dictionary<ChannelViewModel, bool> livePlaybackStates = desired.ToDictionary(
                channel => channel,
                channel => sessions.TryGetValue(channel, out ReceiveStreamSessionRegistry? state) &&
                           state.LivePlaybackEnabled);

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
                    await StartCoreAsync(
                        channel,
                        livePlaybackStates[channel],
                        updateExistingSession: true,
                        cancellationToken).ConfigureAwait(false);
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
        => routeRegistry.SelectSystemDefaultSessions(sessions.ContainsKey);

    // Device-failure recovery must include every session that shares a failed
    // physical mixer route. Default-policy refresh does not use this expansion
    // because the old device is still healthy for fixed-route sessions.
    private ChannelViewModel[] ExpandSharedRouteSessions(ChannelViewModel[] requested)
        => routeRegistry.ExpandSharedRouteSessions(requested);

    public async Task StartAsync(ChannelViewModel channel, CancellationToken cancellationToken = default)
        => await StartCoreAsync(
            channel,
            livePlaybackEnabled: true,
            updateExistingSession: true,
            cancellationToken).ConfigureAwait(false);

    public async Task EnsureDecodeAsync(
        ChannelViewModel channel,
        bool livePlaybackEnabledWhenCreated = false,
        CancellationToken cancellationToken = default)
        => await StartCoreAsync(
            channel,
            livePlaybackEnabledWhenCreated,
            updateExistingSession: false,
            cancellationToken).ConfigureAwait(false);

    public async Task SetLivePlaybackEnabledAsync(
        ChannelViewModel channel,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ObjectDisposedException.ThrowIf(disposed, this);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (sessions.TryGetValue(channel, out ReceiveStreamSessionRegistry? state))
            {
                await state
                    .SetLivePlaybackEnabledAsync(enabled, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task StartCoreAsync(
        ChannelViewModel channel,
        bool livePlaybackEnabled,
        bool updateExistingSession,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ObjectDisposedException.ThrowIf(disposed, this);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (sessions.TryGetValue(channel, out ReceiveStreamSessionRegistry? existingSession))
            {
                if (updateExistingSession)
                {
                    await existingSession
                        .SetLivePlaybackEnabledAsync(livePlaybackEnabled, cancellationToken)
                        .ConfigureAwait(false);
                }
                return;
            }
            if (channel.Definition.IsEncrypted &&
                !CanResolveEncryption(channel.Definition))
            {
                throw new NotSupportedException(
                    "Encrypted receive audio requires a configured key for this protocol; this channel cannot be opened safely.");
            }

            IAudioBackend? createdAudio = null;
            IVocoderBackend? createdVocoder = null;
            ReceiveAudioRoute? createdRoute = null;
            StreamSessionState? createdStreamSession = null;
            ReceiveEpisodePlaybackPool? createdPlaybackPool = null;
            ReceiveStreamSessionRegistry? nextState = null;
            bool sessionRegistered = false;
            bool routeRegistered = false;
            bool routePolicyRegistered = false;

            try
            {
                IVocoderBackend? activeVocoder = channel.Definition.Mode is "dmr" or "p25" or "nxdn"
                    ? vocoderBackend ??= createdVocoder = createVocoderBackend()
                    : null;
                string? requestedDeviceId = getOutputDeviceId?.Invoke(channel);
                bool followsSystemDefault = false;
                ReceiveAudioRoute? activeRoute = requestedDeviceId is not null &&
                    AudioDeviceSelector.HasSpecificRequest(requestedDeviceId) &&
                    routeRegistry.TryGetRoute(requestedDeviceId, out ReceiveAudioRoute? requestedRoute)
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
                double gain = getChannelGain?.Invoke(channel) ?? 1.0;
                double balance = getChannelBalance?.Invoke(channel) ?? 0.0;
                createdPlaybackPool = new ReceiveEpisodePlaybackPool(
                    channel,
                    activeRoute,
                    presentationSamplesObserver);
                ReceiveEpisodePlaybackPool playbackPool = createdPlaybackPool;
                createdStreamSession = await CreateStreamSessionAsync(
                    channel,
                    playbackPool,
                    activeVocoder,
                    gain,
                    balance).ConfigureAwait(false);
                nextState = new ReceiveStreamSessionRegistry(
                    createdStreamSession,
                    () => CreateStreamSessionAsync(
                        channel,
                        playbackPool,
                        activeVocoder,
                        gain,
                        balance),
                    playbackPool,
                    channel,
                    playbackEpisodeResolver,
                    gain,
                    balance,
                    livePlaybackEnabled);
                createdStreamSession = null;
                createdPlaybackPool = null;

                if (!sessions.TryAdd(channel, nextState))
                    throw new InvalidOperationException("The receive channel already has an audio session.");
                sessionRegistered = true;
                if (!routeRegistry.TryAddSessionRoute(channel, activeRoute.DeviceId))
                    throw new InvalidOperationException("The receive channel already has an audio route.");
                routeRegistered = true;
                routeRegistry.AddSessionPolicy(channel, followsSystemDefault);
                routePolicyRegistered = true;
                activeChannels = sessions.Keys.ToArray();
                nextState = null;
                createdAudio = null;
                createdVocoder = null;
                createdRoute = null;
            }
            catch
            {
                if (routePolicyRegistered)
                    routeRegistry.RemoveSessionPolicy(channel);
                if (routeRegistered)
                    routeRegistry.TryRemoveSessionRoute(channel, out _);
                if (sessionRegistered)
                {
                    sessions.TryRemove(channel, out _);
                    activeChannels = sessions.Keys.ToArray();
                }
                if (nextState is not null)
                    await nextState.DisposeAsync().ConfigureAwait(false);
                else if (createdStreamSession is not null)
                    await createdStreamSession.DisposeAsync().ConfigureAwait(false);
                if (createdPlaybackPool is not null)
                    await createdPlaybackPool.DisposeAsync().ConfigureAwait(false);

                if (createdRoute is not null)
                {
                    routeRegistry.TryRemoveRoute(createdRoute.DeviceId, out _);
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

    private async ValueTask<StreamSessionState> CreateStreamSessionAsync(
        ChannelViewModel channel,
        ReceiveEpisodePlaybackPool playbackPool,
        IVocoderBackend? activeVocoder,
        double gain,
        double balance)
        => await receiveSessionFactory
            .CreateAsync(channel, playbackPool, activeVocoder, gain, balance)
            .ConfigureAwait(false);

    private bool CanResolveEncryption(DvmConsole.Core.Runtime.ChannelRuntimeDefinition definition)
        => receiveSessionFactory.CanResolveEncryption(definition);

    public async Task<int> ProcessAsync(
        ChannelViewModel channel,
        FneTrafficFrame traffic,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(traffic);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!sessions.TryGetValue(channel, out ReceiveStreamSessionRegistry? state) || !state.TryAcquire())
            return 0;

        bool entered = false;
        try
        {
            await state.ProcessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            return await state.ProcessAsync(traffic, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (entered)
                state.ProcessGate.Release();
            state.Release();
        }
    }

    public async Task CompleteStreamAsync(
        ChannelViewModel channel,
        uint streamId,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (streamId == 0)
            throw new ArgumentOutOfRangeException(nameof(streamId));
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!sessions.TryGetValue(channel, out ReceiveStreamSessionRegistry? state) || !state.TryAcquire())
            return;

        bool entered = false;
        try
        {
            await state.ProcessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            await state.CompleteStreamAsync(streamId, endedAt, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (entered)
                state.ProcessGate.Release();
            state.Release();
        }
    }

    internal async Task CompleteEpisodeAsync(
        ChannelViewModel channel,
        long episodeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!sessions.TryGetValue(channel, out ReceiveStreamSessionRegistry? state) ||
            !state.TryAcquire())
        {
            return;
        }

        try
        {
            await state.ProcessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await state.CompleteEpisodeAsync(episodeId).ConfigureAwait(false);
            }
            finally
            {
                state.ProcessGate.Release();
            }
        }
        finally
        {
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
            if (sessions.TryRemove(channel, out ReceiveStreamSessionRegistry? state))
            {
                state.BeginStop();
                routeRegistry.TryRemoveSessionRoute(channel, out string? routeId);
                routeRegistry.RemoveSessionPolicy(channel);
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

                if (routeId is not null && !routeRegistry.HasSessionsForRoute(routeId) &&
                    routeRegistry.TryRemoveRoute(routeId, out ReceiveAudioRoute? route))
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
            if (sessions.TryGetValue(channel, out ReceiveStreamSessionRegistry? state))
                await state.SetGainAsync(gain, cancellationToken).ConfigureAwait(false);
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
            if (sessions.TryGetValue(channel, out ReceiveStreamSessionRegistry? state))
                await state.SetBalanceAsync(balance, cancellationToken).ConfigureAwait(false);
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

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        bool startDisposal = false;
        lock (playbackPolicySync)
        {
            if (disposeCompletion is null)
            {
                disposeCompletion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                startDisposal = true;
            }
            completion = disposeCompletion;
        }

        if (startDisposal)
            TaskObservation.Observe(DisposeAndCompleteAsync(completion));
        return new ValueTask(completion.Task);
    }

    private async Task DisposeAndCompleteAsync(TaskCompletionSource completion)
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task DisposeCoreAsync()
    {
        // Recovery owns a sequence of stop/start operations rather than one
        // gate acquisition. Wait for that complete operation before tearing
        // down its gates and routes.
        await recoveryGate.WaitAsync().ConfigureAwait(false);
        try
        {
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
            }
        }
        finally
        {
            recoveryGate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        ReceiveStreamSessionRegistry[] oldSessions = sessions.Values.ToArray();
        sessions.Clear();
        routeRegistry.ClearSessions();
        activeChannels = [];

        foreach (ReceiveStreamSessionRegistry state in oldSessions)
            state.BeginStop();

        Exception? failure = null;
        foreach (ReceiveStreamSessionRegistry state in oldSessions)
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
        ReceiveAudioRoute[] oldRoutes = routeRegistry.RemoveAllRoutes();
        vocoderBackend = null;

        Exception? failure = null;
        foreach (ReceiveAudioRoute route in oldRoutes)
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

    private async Task DisposeRouteAsync(ReceiveAudioRoute route)
    {
        Exception? failure = null;
        try
        {
            await route.Mixer.DisposeAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Mixer faults are published immediately through OutputFailed.
            // Shutdown must not surface the same physical failure again.
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

    private ReceiveAudioRoute GetOrCreateRoute(
        IAudioBackend backend,
        string? requestedDeviceId,
        out ReceiveAudioRoute? createdRoute,
        out bool followsSystemDefault)
    {
        AudioDeviceSelection selection = AudioDeviceSelector.Select(
            backend.EnumerateDevices(AudioDirection.Output),
            AudioDirection.Output,
            requestedDeviceId);
        AudioDeviceInfo output = selection.Device;
        followsSystemDefault = selection.FollowsSystemDefault;

        if (routeRegistry.TryGetRoute(output.Id, out ReceiveAudioRoute? existingRoute))
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
        var mixer = new AudioMixer(playback);
        var route = new ReceiveAudioRoute(output.Id, backend, mixer);
        mixer.Faulted += exception => NotifyOutputFailure(route.DeviceId, exception);
        lock (playbackPolicySync)
        {
            if (!routeRegistry.TryAddRoute(route))
                throw new InvalidOperationException("The receive output route was added concurrently.");
            if (transitionPlaybackDiscarded || operatorOutputMuted)
                route.Mixer.SetInputDiscarded(discarded: true);
        }
        createdRoute = route;
        return route;
    }

    private void NotifyOutputFailure(string deviceId, Exception exception)
    {
        ChannelViewModel[] affectedChannels = routeRegistry.GetSessionsForRoute(deviceId);
        if (affectedChannels.Length == 0)
            return;
        OutputFailed?.Invoke(new ReceiveAudioOutputFailure(deviceId, affectedChannels, exception));
    }

    private sealed class ReceiveStreamSessionRegistry : IAsyncDisposable
    {
        private const int MaximumStreamSessions = 8;
        private static readonly TimeSpan CompletedStreamRetention = TimeSpan.FromSeconds(1);
        private readonly AsyncOperationLifetime operationLifetime = new();
        private readonly object streamSync = new();
        private readonly Dictionary<uint, StreamSessionState> streams = [];
        private readonly ReceiveStreamLifecycle receiveLifecycle =
            ReceiveStreamLifecycle.CreateDefault();
        private readonly Func<ValueTask<StreamSessionState>> createStreamSession;
        private readonly ReceiveEpisodePlaybackPool playbackPool;
        private readonly ChannelViewModel channel;
        private readonly Func<ChannelViewModel, FneTrafficFrame, ReceivePlaybackEpisode>
            playbackEpisodeResolver;
        private StreamSessionState? unboundStream;
        private ReceiveAudioDiagnostics completedDiagnostics = new(0, 0, 0, 0);
        private double gain;
        private double balance;
        private bool livePlaybackEnabled;

        public ReceiveStreamSessionRegistry(
            StreamSessionState initialStream,
            Func<ValueTask<StreamSessionState>> createStreamSession,
            ReceiveEpisodePlaybackPool playbackPool,
            ChannelViewModel channel,
            Func<ChannelViewModel, FneTrafficFrame, ReceivePlaybackEpisode> playbackEpisodeResolver,
            double gain,
            double balance,
            bool livePlaybackEnabled)
        {
            unboundStream = initialStream ?? throw new ArgumentNullException(nameof(initialStream));
            this.createStreamSession = createStreamSession ?? throw new ArgumentNullException(nameof(createStreamSession));
            this.playbackPool = playbackPool ?? throw new ArgumentNullException(nameof(playbackPool));
            this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
            this.playbackEpisodeResolver = playbackEpisodeResolver ??
                throw new ArgumentNullException(nameof(playbackEpisodeResolver));
            this.gain = gain;
            this.balance = balance;
            this.livePlaybackEnabled = livePlaybackEnabled;
            initialStream.Session.SetLivePlaybackEnabled(livePlaybackEnabled);
        }

        public SemaphoreSlim ProcessGate { get; } = new(1, 1);

        public bool LivePlaybackEnabled
        {
            get
            {
                lock (streamSync)
                    return livePlaybackEnabled;
            }
        }

        public async ValueTask<int> ProcessAsync(
            FneTrafficFrame traffic,
            CancellationToken cancellationToken)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            await RemoveExpiredCompletedStreamsAsync(now).ConfigureAwait(false);
            await CompleteExpiredStreamsAsync(now, cancellationToken).ConfigureAwait(false);

            ReceiveStreamDecision lifecycleDecision = ObserveTraffic(traffic, now);
            if (!lifecycleDecision.AcceptTraffic)
                return 0;

            if (lifecycleDecision.Transition == ReceiveStreamTransition.Restarted &&
                FindStream(traffic.StreamId) is StreamSessionState restarted)
            {
                await CompleteTrackedStreamAsync(restarted, now, cancellationToken)
                    .ConfigureAwait(false);
            }

            bool terminating = ReceiveTrafficClassifier.IsTerminator(traffic);
            StreamSessionState? stream = terminating
                ? FindStream(traffic.StreamId)
                : await GetOrCreateStreamAsync(traffic.StreamId, now).ConfigureAwait(false);
            if (stream is null)
                return 0;

            stream.SampleContext.Set(traffic.StreamId, traffic.SourceId);
            stream.EpisodePlayback.Bind(playbackEpisodeResolver(channel, traffic));
            try
            {
                return await stream.Session.ProcessAsync(traffic, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                stream.SampleContext.Clear();
                stream.LastActivity = now;
                if (terminating)
                    await CompleteTrackedStreamAsync(stream, now, cancellationToken)
                        .ConfigureAwait(false);
            }
        }

        public async ValueTask CompleteStreamAsync(
            uint streamId,
            DateTimeOffset endedAt,
            CancellationToken cancellationToken)
        {
            receiveLifecycle.Complete(streamId, endedAt);
            StreamSessionState? stream = FindStream(streamId);
            if (stream is not null)
            {
                await CompleteTrackedStreamAsync(stream, endedAt, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        public ValueTask CompleteEpisodeAsync(long episodeId)
            => playbackPool.CompleteEpisodeAsync(episodeId);

        public EpisodeLivePlayoutDiagnostics GetPlaybackArbitrationDiagnostics()
            => playbackPool.GetDiagnostics();

        public ReceiveAudioDiagnostics GetDiagnostics()
        {
            StreamSessionState[] snapshot;
            ReceiveAudioDiagnostics completed;
            lock (streamSync)
            {
                snapshot = streams.Values
                    .Concat(unboundStream is null ? [] : [unboundStream])
                    .ToArray();
                completed = completedDiagnostics;
            }

            int decoded = completed.FramesDecoded;
            long lost = completed.LostPackets;
            long late = completed.DuplicateOrLatePackets;
            long malformed = completed.MalformedPackets;
            foreach (StreamSessionState stream in snapshot)
            {
                ReceiveAudioDiagnostics current = stream.Session.GetDiagnostics();
                decoded = checked(decoded + current.FramesDecoded);
                lost = checked(lost + current.LostPackets);
                late = checked(late + current.DuplicateOrLatePackets);
                malformed = checked(malformed + current.MalformedPackets);
            }
            return new ReceiveAudioDiagnostics(decoded, lost, late, malformed);
        }

        public bool IsTrackingStream(uint streamId)
        {
            lock (streamSync)
                return streams.TryGetValue(streamId, out StreamSessionState? stream) &&
                       stream.CompletedAt is null;
        }

        public async Task SetGainAsync(
            double nextGain,
            CancellationToken cancellationToken)
        {
            await ProcessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                StreamSessionState[] snapshot;
                lock (streamSync)
                {
                    gain = nextGain;
                    snapshot = GetActiveStreamsLocked();
                }
                foreach (StreamSessionState stream in snapshot)
                    stream.Session.SetGain(nextGain);
            }
            finally
            {
                ProcessGate.Release();
            }
        }

        public async Task SetBalanceAsync(
            double nextBalance,
            CancellationToken cancellationToken)
        {
            await ProcessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                StreamSessionState[] snapshot;
                lock (streamSync)
                {
                    balance = nextBalance;
                    snapshot = GetActiveStreamsLocked();
                }
                foreach (StreamSessionState stream in snapshot)
                    stream.Session.SetBalance(nextBalance);
            }
            finally
            {
                ProcessGate.Release();
            }
        }

        public async Task SetLivePlaybackEnabledAsync(
            bool enabled,
            CancellationToken cancellationToken)
        {
            await ProcessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                StreamSessionState[] snapshot;
                lock (streamSync)
                {
                    livePlaybackEnabled = enabled;
                    snapshot = GetActiveStreamsLocked();
                }
                foreach (StreamSessionState stream in snapshot)
                    stream.Session.SetLivePlaybackEnabled(enabled);
            }
            finally
            {
                ProcessGate.Release();
            }
        }

        // Called only while streamSync is held. Completed streams remain as
        // short-lived tombstones, but their mixer lanes have already drained.
        private StreamSessionState[] GetActiveStreamsLocked()
            => streams.Values
                .Where(stream => stream.CompletedAt is null)
                .Concat(unboundStream is null ? [] : [unboundStream])
                .ToArray();

        public bool TryAcquire()
            => operationLifetime.TryAcquire();

        public void Release()
            => operationLifetime.Release();

        public void BeginStop()
            => operationLifetime.BeginStop();

        public Task WaitForIdleAsync() => operationLifetime.WaitForIdleAsync();

        public async ValueTask DisposeAsync()
        {
            StreamSessionState[] oldStreams;
            lock (streamSync)
            {
                oldStreams = streams.Values
                    .Concat(unboundStream is null ? [] : [unboundStream])
                    .Distinct()
                    .ToArray();
                streams.Clear();
                unboundStream = null;
            }
            Exception? failure = null;
            foreach (StreamSessionState stream in oldStreams)
            {
                try
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (exception.GetBaseException() is IOException)
                {
                    // The owning mixer route already published this failure.
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }
            }
            try
            {
                await playbackPool.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception.GetBaseException() is IOException)
            {
                // The owning mixer route already published this failure.
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
            ProcessGate.Dispose();
            if (failure is not null)
                throw failure;
        }

        private StreamSessionState? FindStream(uint streamId)
        {
            lock (streamSync)
                return streams.GetValueOrDefault(streamId);
        }

        private async ValueTask<StreamSessionState> GetOrCreateStreamAsync(
            uint streamId,
            DateTimeOffset now)
        {
            StreamSessionState? evicted = null;
            lock (streamSync)
            {
                if (streams.TryGetValue(streamId, out StreamSessionState? existing) &&
                    existing.CompletedAt is null)
                {
                    return existing;
                }

                if (existing is not null)
                {
                    streams.Remove(streamId);
                    AccumulateDiagnostics(existing);
                    evicted = existing;
                }
                else if (unboundStream is not null)
                {
                    StreamSessionState initial = unboundStream;
                    unboundStream = null;
                    initial.StreamId = streamId;
                    initial.LastActivity = now;
                    streams.Add(streamId, initial);
                    return initial;
                }
                else if (streams.Count >= MaximumStreamSessions)
                {
                    KeyValuePair<uint, StreamSessionState> oldest = streams
                        .OrderBy(pair => pair.Value.CompletedAt is null ? 1 : 0)
                        .ThenBy(pair => pair.Value.LastActivity)
                        .First();
                    streams.Remove(oldest.Key);
                    AccumulateDiagnostics(oldest.Value);
                    receiveLifecycle.ObserveTerminator(oldest.Key, now);
                    evicted = oldest.Value;
                }
            }

            if (evicted is not null)
                await evicted.DisposeAsync().ConfigureAwait(false);

            StreamSessionState created = await createStreamSession().ConfigureAwait(false);
            lock (streamSync)
            {
                created.StreamId = streamId;
                created.LastActivity = now;
                created.Session.SetGain(gain);
                created.Session.SetBalance(balance);
                created.Session.SetLivePlaybackEnabled(livePlaybackEnabled);
                streams.Add(streamId, created);
            }
            return created;
        }

        private ReceiveStreamDecision ObserveTraffic(
            FneTrafficFrame traffic,
            DateTimeOffset now)
        {
            if (ReceiveTrafficClassifier.IsTerminator(traffic))
                return receiveLifecycle.ObserveTerminator(traffic.StreamId, now);
            if (ReceiveTrafficClassifier.IsDmrPrivacyHeader(traffic))
            {
                return receiveLifecycle.IsActive(traffic.StreamId)
                    ? receiveLifecycle.ObserveVoice(traffic.StreamId, now)
                    : default;
            }
            if (ReceiveTrafficClassifier.IsDefinitiveStart(traffic))
                return receiveLifecycle.ObserveDefinitiveStart(traffic.StreamId, now);
            return ReceiveTrafficClassifier.CarriesVoicePayload(traffic)
                ? receiveLifecycle.ObserveVoice(traffic.StreamId, now)
                : default;
        }

        private async ValueTask CompleteExpiredStreamsAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                ReceiveStreamDecision decision = receiveLifecycle.Advance(now);
                if (decision.Transition == ReceiveStreamTransition.GraceStarted)
                    continue;
                if (decision.Transition is not (
                        ReceiveStreamTransition.GraceExpired or
                        ReceiveStreamTransition.TerminationExpired) ||
                    decision.EndedStreamId is not uint streamId)
                {
                    return;
                }

                StreamSessionState? stream = FindStream(streamId);
                if (stream is not null)
                {
                    await CompleteTrackedStreamAsync(stream, now, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        private async ValueTask CompleteTrackedStreamAsync(
            StreamSessionState stream,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken)
        {
            lock (streamSync)
            {
                if (stream.CompletedAt is not null)
                    return;
            }

            await stream.Session.CompletePlaybackAsync(cancellationToken).ConfigureAwait(false);
            lock (streamSync)
                stream.CompletedAt = completedAt;
        }

        private async ValueTask RemoveExpiredCompletedStreamsAsync(DateTimeOffset now)
        {
            StreamSessionState[] expired;
            lock (streamSync)
            {
                expired = streams.Values
                    .Where(stream => stream.CompletedAt is DateTimeOffset completedAt &&
                        now - completedAt >= CompletedStreamRetention)
                    .ToArray();
                foreach (StreamSessionState stream in expired)
                {
                    streams.Remove(stream.StreamId);
                    AccumulateDiagnostics(stream);
                }
            }
            foreach (StreamSessionState stream in expired)
                await stream.DisposeAsync().ConfigureAwait(false);
        }

        // Called only while streamSync is held.
        private void AccumulateDiagnostics(StreamSessionState stream)
        {
            ReceiveAudioDiagnostics current = stream.Session.GetDiagnostics();
            completedDiagnostics = new ReceiveAudioDiagnostics(
                checked(completedDiagnostics.FramesDecoded + current.FramesDecoded),
                checked(completedDiagnostics.LostPackets + current.LostPackets),
                checked(completedDiagnostics.DuplicateOrLatePackets + current.DuplicateOrLatePackets),
                checked(completedDiagnostics.MalformedPackets + current.MalformedPackets));
        }
    }

}

public sealed record ReceiveRouteRecoveryResult(
    IReadOnlyList<ChannelViewModel> Restarted,
    IReadOnlyList<ChannelViewModel> Failed,
    string? Diagnostic);

public sealed record ReceiveAudioOutputFailure(
    string DeviceId,
    IReadOnlyList<ChannelViewModel> AffectedChannels,
    Exception Exception);
