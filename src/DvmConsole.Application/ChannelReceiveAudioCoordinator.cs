using System.Collections.Concurrent;
using DvmConsole.Audio;
using DvmConsole.Core.Runtime;
using DvmConsole.Media;
using DvmConsole.Operations;
using DvmConsole.Vocoder;

namespace DvmConsole.Application;

internal readonly record struct ReceiveAudioProcessTiming(
    int FramesDecoded,
    TimeSpan SessionGateDelay,
    TimeSpan SessionProcessingDuration,
    bool? EncryptedSessionProcessing,
    bool Measured);

internal readonly record struct ReceiveStreamProcessResult(
    int FramesDecoded,
    bool? Encrypted);

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
    private readonly ConcurrentDictionary<ChannelId, ReceiveStreamSessionRegistry> sessions = [];
    private readonly ReceiveAudioRouteRegistry routeRegistry = new();
    private readonly Func<IAudioBackend> createAudioBackend;
    private readonly Func<IVocoderBackend> createVocoderBackend;
    private readonly Func<ChannelId, double>? getChannelGain;
    private readonly Func<ChannelId, double>? getChannelBalance;
    private readonly Func<ChannelId, string?>? getOutputDeviceId;
    private readonly ReceiveSessionFactory receiveSessionFactory;
    private readonly IClock clock;
    private readonly TimeProvider timeProvider;
    private readonly Action<ChannelId, uint, ReadOnlyMemory<short>, TimeSpan>?
        presentationSamplesObserver;
    private Func<ChannelId, IRadioMediaFrame, ReceivePlaybackEpisode> playbackEpisodeResolver =
        static (_, traffic) => new ReceivePlaybackEpisode(
            traffic.StreamId,
            traffic.StreamId,
            traffic.StreamId,
            RetainUntilEpisodeCompletion: false);
    private volatile ChannelId[] activeChannels = [];
    private IVocoderBackend? vocoderBackend;
    private bool transitionPlaybackDiscarded;
    private bool operatorOutputMuted;
    private bool disposed;
    private TaskCompletionSource? disposeCompletion;

    public ChannelReceiveAudioCoordinator(
        Func<IAudioBackend> createAudioBackend,
        Func<IVocoderBackend> createVocoderBackend,
        IP25KeyResolver? p25KeyResolver = null,
        Action<ChannelId, uint, uint, ReadOnlyMemory<short>>? samplesObserver = null,
        Func<ChannelId, double>? getChannelGain = null,
        Func<ChannelId, string?>? getOutputDeviceId = null,
        IDmrKeyResolver? dmrKeyResolver = null,
        INxdnKeyResolver? nxdnKeyResolver = null,
        Func<ChannelId, double>? getChannelBalance = null,
        Action<ChannelId, uint, ReadOnlyMemory<short>, TimeSpan>?
            presentationSamplesObserver = null,
        IClock? clock = null,
        TimeProvider? timeProvider = null)
    {
        this.createAudioBackend = createAudioBackend ?? throw new ArgumentNullException(nameof(createAudioBackend));
        this.createVocoderBackend = createVocoderBackend ?? throw new ArgumentNullException(nameof(createVocoderBackend));
        this.getChannelGain = getChannelGain;
        this.getChannelBalance = getChannelBalance;
        this.getOutputDeviceId = getOutputDeviceId;
        this.presentationSamplesObserver = presentationSamplesObserver;
        this.clock = clock ?? SystemClock.Instance;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        receiveSessionFactory = new ReceiveSessionFactory(
            p25KeyResolver,
            dmrKeyResolver,
            nxdnKeyResolver,
            samplesObserver);
    }

    internal void SetReceivePlaybackEpisodeResolver(
        Func<ChannelId, IRadioMediaFrame, ReceivePlaybackEpisode> resolver)
        => playbackEpisodeResolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public ChannelId? ActiveChannel => activeChannels.Length == 0 ? null : activeChannels[0];
    public event Action<ReceiveAudioOutputFailure>? OutputFailed;
    public IReadOnlyList<ChannelId> ActiveChannels => activeChannels;
    public IReadOnlyList<ChannelId> LivePlaybackChannels => activeChannels
        .Where(IsLivePlaybackEnabled)
        .ToArray();

    public bool IsActive(ChannelId channelId)
    {
        return Array.IndexOf(activeChannels, channelId) >= 0;
    }

    public bool IsLivePlaybackEnabled(ChannelId channelId)
    {
        return sessions.TryGetValue(channelId, out ReceiveStreamSessionRegistry? state) &&
               state.LivePlaybackEnabled;
    }

    public bool IsTrackingStream(ChannelId channelId, uint streamId)
    {
        return streamId != 0 &&
               sessions.TryGetValue(channelId, out ReceiveStreamSessionRegistry? state) &&
               state.IsTrackingStream(streamId);
    }

    public ReceiveAudioDiagnostics GetDiagnostics(ChannelId channelId)
    {
        return sessions.TryGetValue(channelId, out ReceiveStreamSessionRegistry? state)
            ? state.GetDiagnostics()
            : new ReceiveAudioDiagnostics(0, 0, 0, 0);
    }

    public AudioMixerDiagnostics? GetPlaybackDiagnostics(ChannelId channelId)
    {
        return routeRegistry.TryGetRoute(channelId, out ReceiveAudioRoute? route)
            ? route.Mixer.GetDiagnostics()
            : null;
    }

    internal EpisodeLivePlayoutDiagnostics GetPlaybackArbitrationDiagnostics(
        ChannelId channelId)
    {
        return sessions.TryGetValue(channelId, out ReceiveStreamSessionRegistry? state)
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
        ChannelId channelId,
        CancellationToken cancellationToken = default)
    {
        if (!IsActive(channelId))
            return false;
        ReceiveRouteRecoveryResult result = await RecoverSelectedAsync([channelId], cancellationToken).ConfigureAwait(false);
        return result.Restarted.Contains(channelId);
    }

    public async Task<ReceiveRouteRecoveryResult> RecoverSelectedAsync(
        IReadOnlyCollection<ChannelId> desiredChannels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(desiredChannels);
        ChannelId[] requested = desiredChannels
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
        Func<ChannelId[]> selectChannels,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await recoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ChannelId[] desired;
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

            Dictionary<ChannelId, ReceiveChannelDescriptor> descriptors = desired.ToDictionary(
                channelId => channelId,
                channelId => sessions[channelId].Channel);
            Dictionary<ChannelId, bool> livePlaybackStates = desired.ToDictionary(
                channelId => channelId,
                channelId => sessions.TryGetValue(channelId, out ReceiveStreamSessionRegistry? state) &&
                           state.LivePlaybackEnabled);

            Exception? stopFailure = null;
            foreach (ChannelId channelId in desired)
            {
                try
                {
                    await StopAsync(channelId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    stopFailure ??= exception;
                }
            }

            var restarted = new List<ChannelId>();
            var failed = new List<ChannelId>();
            foreach (ChannelId channelId in desired)
            {
                try
                {
                    await StartCoreAsync(
                        descriptors[channelId],
                        livePlaybackStates[channelId],
                        updateExistingSession: true,
                        cancellationToken).ConfigureAwait(false);
                    if (IsActive(channelId))
                        restarted.Add(channelId);
                    else
                        failed.Add(channelId);
                }
                catch
                {
                    failed.Add(channelId);
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
    private ChannelId[] SelectSystemDefaultSessions()
        => routeRegistry.SelectSystemDefaultSessions(sessions.ContainsKey);

    // Device-failure recovery must include every session that shares a failed
    // physical mixer route. Default-policy refresh does not use this expansion
    // because the old device is still healthy for fixed-route sessions.
    private ChannelId[] ExpandSharedRouteSessions(ChannelId[] requested)
        => routeRegistry.ExpandSharedRouteSessions(requested);

    public async Task StartAsync(ReceiveChannelDescriptor channel, CancellationToken cancellationToken = default)
        => await StartCoreAsync(
            channel,
            livePlaybackEnabled: true,
            updateExistingSession: true,
            cancellationToken).ConfigureAwait(false);

    public async Task EnsureDecodeAsync(
        ReceiveChannelDescriptor channel,
        bool livePlaybackEnabledWhenCreated = false,
        CancellationToken cancellationToken = default)
        => await StartCoreAsync(
            channel,
            livePlaybackEnabledWhenCreated,
            updateExistingSession: false,
            cancellationToken).ConfigureAwait(false);

    public async Task SetLivePlaybackEnabledAsync(
        ChannelId channelId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (sessions.TryGetValue(channelId, out ReceiveStreamSessionRegistry? state))
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
        ReceiveChannelDescriptor channel,
        bool livePlaybackEnabled,
        bool updateExistingSession,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ObjectDisposedException.ThrowIf(disposed, this);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (sessions.TryGetValue(channel.Id, out ReceiveStreamSessionRegistry? existingSession))
            {
                if (updateExistingSession)
                {
                    await existingSession
                        .SetLivePlaybackEnabledAsync(livePlaybackEnabled, cancellationToken)
                        .ConfigureAwait(false);
                }
                return;
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
                IVocoderBackend? activeVocoder = ChannelVocoderPolicy.RequiresVocoder(
                    channel.Definition.Protocol)
                    ? vocoderBackend ??= createdVocoder = createVocoderBackend()
                    : null;
                string? requestedDeviceId = getOutputDeviceId?.Invoke(channel.Id);
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
                double gain = getChannelGain?.Invoke(channel.Id) ?? 1.0;
                double balance = getChannelBalance?.Invoke(channel.Id) ?? 0.0;
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
                    clock,
                    gain,
                    balance,
                    livePlaybackEnabled);
                createdStreamSession = null;
                createdPlaybackPool = null;

                if (!sessions.TryAdd(channel.Id, nextState))
                    throw new InvalidOperationException("The receive channel already has an audio session.");
                sessionRegistered = true;
                if (!routeRegistry.TryAddSessionRoute(channel.Id, activeRoute.DeviceId))
                    throw new InvalidOperationException("The receive channel already has an audio route.");
                routeRegistered = true;
                routeRegistry.AddSessionPolicy(channel.Id, followsSystemDefault);
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
                    routeRegistry.RemoveSessionPolicy(channel.Id);
                if (routeRegistered)
                    routeRegistry.TryRemoveSessionRoute(channel.Id, out _);
                if (sessionRegistered)
                {
                    sessions.TryRemove(channel.Id, out _);
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
        ReceiveChannelDescriptor channel,
        ReceiveEpisodePlaybackPool playbackPool,
        IVocoderBackend? activeVocoder,
        double gain,
        double balance)
        => await receiveSessionFactory
            .CreateAsync(channel, playbackPool, activeVocoder, gain, balance)
            .ConfigureAwait(false);

    public async Task<int> ProcessAsync(
        ChannelId channelId,
        IRadioMediaFrame traffic,
        CancellationToken cancellationToken = default)
        => (await ProcessWithTimingAsync(channelId, traffic, cancellationToken).ConfigureAwait(false))
            .FramesDecoded;

    internal async Task<ReceiveAudioProcessTiming> ProcessWithTimingAsync(
        ChannelId channelId,
        IRadioMediaFrame traffic,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!sessions.TryGetValue(channelId, out ReceiveStreamSessionRegistry? state) || !state.TryAcquire())
            return new ReceiveAudioProcessTiming(
                FramesDecoded: 0,
                SessionGateDelay: TimeSpan.Zero,
                SessionProcessingDuration: TimeSpan.Zero,
                EncryptedSessionProcessing: null,
                Measured: false);

        bool entered = false;
        try
        {
            long gateStarted = timeProvider.GetTimestamp();
            await state.ProcessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            TimeSpan gateDelay = timeProvider.GetElapsedTime(gateStarted);
            entered = true;
            long processingStarted = timeProvider.GetTimestamp();
            ReceiveStreamProcessResult result = await state
                .ProcessAsync(traffic, cancellationToken)
                .ConfigureAwait(false);
            return new ReceiveAudioProcessTiming(
                result.FramesDecoded,
                gateDelay,
                timeProvider.GetElapsedTime(processingStarted),
                result.Encrypted,
                Measured: true);
        }
        finally
        {
            if (entered)
                state.ProcessGate.Release();
            state.Release();
        }
    }

    public async Task CompleteStreamAsync(
        ChannelId channelId,
        uint streamId,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken = default)
    {
        if (streamId == 0)
            throw new ArgumentOutOfRangeException(nameof(streamId));
        ObjectDisposedException.ThrowIf(disposed, this);

        if (!sessions.TryGetValue(channelId, out ReceiveStreamSessionRegistry? state) || !state.TryAcquire())
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
        ChannelId channelId,
        long episodeId,
        CancellationToken cancellationToken = default)
    {
        if (!sessions.TryGetValue(channelId, out ReceiveStreamSessionRegistry? state) ||
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

    public async Task StopAsync(ChannelId channelId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Exception? failure = null;
            if (sessions.TryRemove(channelId, out ReceiveStreamSessionRegistry? state))
            {
                state.BeginStop();
                routeRegistry.TryRemoveSessionRoute(channelId, out string? routeId);
                routeRegistry.RemoveSessionPolicy(channelId);
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

            if (sessions.IsEmpty)
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

    public async Task SetGainAsync(ChannelId channelId, double gain, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (sessions.TryGetValue(channelId, out ReceiveStreamSessionRegistry? state))
                await state.SetGainAsync(gain, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SetBalanceAsync(ChannelId channelId, double balance, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (sessions.TryGetValue(channelId, out ReceiveStreamSessionRegistry? state))
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
            _ = DisposeAndCompleteAsync(completion);
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
        ChannelId[] affectedChannels = routeRegistry.GetSessionsForRoute(deviceId);
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
        private readonly ReceiveChannelDescriptor channel;
        private readonly Func<ChannelId, IRadioMediaFrame, ReceivePlaybackEpisode>
            playbackEpisodeResolver;
        private readonly IClock clock;
        private StreamSessionState? unboundStream;
        private ReceiveAudioDiagnostics completedDiagnostics = new(0, 0, 0, 0);
        private double gain;
        private double balance;
        private bool livePlaybackEnabled;

        public ReceiveStreamSessionRegistry(
            StreamSessionState initialStream,
            Func<ValueTask<StreamSessionState>> createStreamSession,
            ReceiveEpisodePlaybackPool playbackPool,
            ReceiveChannelDescriptor channel,
            Func<ChannelId, IRadioMediaFrame, ReceivePlaybackEpisode> playbackEpisodeResolver,
            IClock clock,
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
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.gain = gain;
            this.balance = balance;
            this.livePlaybackEnabled = livePlaybackEnabled;
            initialStream.Encrypted = GetInitialEncryptionState(channel);
            initialStream.Session.SetLivePlaybackEnabled(livePlaybackEnabled);
        }

        public SemaphoreSlim ProcessGate { get; } = new(1, 1);
        public ReceiveChannelDescriptor Channel => channel;

        public bool LivePlaybackEnabled
        {
            get
            {
                lock (streamSync)
                    return livePlaybackEnabled;
            }
        }

        public async ValueTask<ReceiveStreamProcessResult> ProcessAsync(
            IRadioMediaFrame traffic,
            CancellationToken cancellationToken)
        {
            DateTimeOffset now = clock.UtcNow;
            await RemoveExpiredCompletedStreamsAsync(now).ConfigureAwait(false);
            await CompleteExpiredStreamsAsync(now, cancellationToken).ConfigureAwait(false);

            ReceiveStreamDecision lifecycleDecision = ObserveTraffic(traffic, now);
            if (lifecycleDecision.Transition == ReceiveStreamTransition.TerminationPending)
            {
                // The receive worker has already drained every earlier jittered
                // voice packet. Release the physical decoder and mixer producer
                // now while the logical episode remains available for late voice.
                await CompletePhysicalStreamIfPresentAsync(
                        traffic.StreamId,
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
                return default;
            }
            if (!lifecycleDecision.AcceptTraffic)
                return default;

            if (lifecycleDecision.Transition == ReceiveStreamTransition.Restarted &&
                FindStream(traffic.StreamId) is StreamSessionState restarted)
            {
                await CompletePhysicalStreamAsync(restarted, now, cancellationToken)
                    .ConfigureAwait(false);
            }

            StreamSessionState stream = await GetOrCreateStreamAsync(traffic.StreamId, now)
                .ConfigureAwait(false);

            if (RadioFrameEncryptionResolver.TryResolve(traffic) is
                RadioFrameEncryption encryption)
                stream.Encrypted = encryption.IsSecure;

            stream.SampleContext.Set(traffic.StreamId, traffic.SourceId);
            stream.EpisodePlayback.Bind(playbackEpisodeResolver(channel.Id, traffic));
            try
            {
                int framesDecoded = await stream.Session
                    .ProcessAsync(traffic, cancellationToken)
                    .ConfigureAwait(false);
                return new ReceiveStreamProcessResult(framesDecoded, stream.Encrypted);
            }
            finally
            {
                stream.SampleContext.Clear();
                stream.LastActivity = now;
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
                await CompletePhysicalStreamAsync(stream, endedAt, cancellationToken)
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
                created.Encrypted = GetInitialEncryptionState(channel);
                created.Session.SetGain(gain);
                created.Session.SetBalance(balance);
                created.Session.SetLivePlaybackEnabled(livePlaybackEnabled);
                streams.Add(streamId, created);
            }
            return created;
        }

        private static bool? GetInitialEncryptionState(ReceiveChannelDescriptor channel)
        {
            if (channel.Definition.Protocol == ChannelProtocol.Analog)
                return false;
            return null;
        }

        private ReceiveStreamDecision ObserveTraffic(
            IRadioMediaFrame traffic,
            DateTimeOffset now)
        {
            if (RadioReceiveTrafficClassifier.IsTerminator(traffic))
                return receiveLifecycle.ObserveTerminator(traffic.StreamId, now);
            if (RadioReceiveTrafficClassifier.IsDmrPrivacyHeader(traffic))
            {
                return receiveLifecycle.IsActive(traffic.StreamId)
                    ? receiveLifecycle.ObserveVoice(traffic.StreamId, now)
                    : default;
            }
            if (RadioReceiveTrafficClassifier.IsDefinitiveStart(traffic))
                return receiveLifecycle.ObserveDefinitiveStart(traffic.StreamId, now);
            return RadioReceiveTrafficClassifier.CarriesVoicePayload(traffic)
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
                    await CompletePhysicalStreamAsync(stream, now, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        private async ValueTask CompletePhysicalStreamIfPresentAsync(
            uint streamId,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken)
        {
            StreamSessionState? stream = FindStream(streamId);
            if (stream is not null)
            {
                await CompletePhysicalStreamAsync(stream, completedAt, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private async ValueTask CompletePhysicalStreamAsync(
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
    IReadOnlyList<ChannelId> Restarted,
    IReadOnlyList<ChannelId> Failed,
    string? Diagnostic);

public sealed record ReceiveAudioOutputFailure(
    string DeviceId,
    IReadOnlyList<ChannelId> AffectedChannels,
    Exception Exception);
