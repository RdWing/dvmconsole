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
    private readonly ConcurrentDictionary<ChannelViewModel, SessionState> sessions = [];
    private readonly ConcurrentDictionary<ChannelViewModel, string> sessionRoutes = [];
    private readonly Dictionary<ChannelViewModel, bool> sessionFollowsSystemDefault = [];
    private readonly ConcurrentDictionary<string, AudioRoute> audioRoutes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<IAudioBackend> createAudioBackend;
    private readonly Func<IVocoderBackend> createVocoderBackend;
    private readonly IP25KeyResolver? p25KeyResolver;
    private readonly IDmrKeyResolver? dmrKeyResolver;
    private readonly INxdnKeyResolver? nxdnKeyResolver;
    private readonly Action<ChannelViewModel, uint, uint, ReadOnlyMemory<short>>? samplesObserver;
    private readonly Action<ChannelViewModel, uint, ReadOnlyMemory<short>, TimeSpan>?
        presentationSamplesObserver;
    private readonly Func<ChannelViewModel, double>? getChannelGain;
    private readonly Func<ChannelViewModel, double>? getChannelBalance;
    private readonly Func<ChannelViewModel, string?>? getOutputDeviceId;
    private volatile ChannelViewModel[] activeChannels = [];
    private IVocoderBackend? vocoderBackend;
    private bool livePlaybackDiscarded;
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
        this.p25KeyResolver = p25KeyResolver;
        this.dmrKeyResolver = dmrKeyResolver;
        this.nxdnKeyResolver = nxdnKeyResolver;
        this.samplesObserver = samplesObserver;
        this.presentationSamplesObserver = presentationSamplesObserver;
        this.getChannelGain = getChannelGain;
        this.getChannelBalance = getChannelBalance;
        this.getOutputDeviceId = getOutputDeviceId;
    }

    public ChannelViewModel? ActiveChannel => activeChannels.FirstOrDefault();
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
        return sessions.TryGetValue(channel, out SessionState? state) &&
               state.LivePlaybackEnabled;
    }

    public bool IsTrackingStream(ChannelViewModel channel, uint streamId)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return streamId != 0 &&
               sessions.TryGetValue(channel, out SessionState? state) &&
               state.IsTrackingStream(streamId);
    }

    public ReceiveAudioDiagnostics GetDiagnostics(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return sessions.TryGetValue(channel, out SessionState? state)
            ? state.GetDiagnostics()
            : new ReceiveAudioDiagnostics(0, 0, 0, 0);
    }

    public AudioMixerDiagnostics? GetPlaybackDiagnostics(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return sessionRoutes.TryGetValue(channel, out string? routeId) &&
               audioRoutes.TryGetValue(routeId, out AudioRoute? route)
            ? route.Mixer.GetDiagnostics()
            : null;
    }

    public long SetLivePlaybackDiscarded(bool discarded)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (playbackPolicySync)
        {
            livePlaybackDiscarded = discarded;
            long totalDiscardedSamples = 0;
            foreach (AudioRoute route in audioRoutes.Values)
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

            Dictionary<ChannelViewModel, bool> livePlaybackStates = desired.ToDictionary(
                channel => channel,
                channel => sessions.TryGetValue(channel, out SessionState? state) &&
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
            if (sessions.TryGetValue(channel, out SessionState? state))
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
            if (sessions.TryGetValue(channel, out SessionState? existingSession))
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
            AudioRoute? createdRoute = null;
            StreamSessionState? createdStreamSession = null;
            SessionState? nextState = null;
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
                double gain = getChannelGain?.Invoke(channel) ?? 1.0;
                double balance = getChannelBalance?.Invoke(channel) ?? 0.0;
                createdStreamSession = await CreateStreamSessionAsync(
                    channel,
                    activeRoute,
                    activeVocoder,
                    gain,
                    balance).ConfigureAwait(false);
                nextState = new SessionState(
                    createdStreamSession,
                    () => CreateStreamSessionAsync(channel, activeRoute, activeVocoder, gain, balance),
                    gain,
                    balance,
                    livePlaybackEnabled);
                createdStreamSession = null;

                if (!sessions.TryAdd(channel, nextState))
                    throw new InvalidOperationException("The receive channel already has an audio session.");
                sessionRegistered = true;
                if (!sessionRoutes.TryAdd(channel, activeRoute.DeviceId))
                    throw new InvalidOperationException("The receive channel already has an audio route.");
                routeRegistered = true;
                sessionFollowsSystemDefault.Add(channel, followsSystemDefault);
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
                    sessionFollowsSystemDefault.Remove(channel);
                if (routeRegistered)
                    sessionRoutes.TryRemove(channel, out _);
                if (sessionRegistered)
                {
                    sessions.TryRemove(channel, out _);
                    activeChannels = sessions.Keys.ToArray();
                }
                if (nextState is not null)
                    await nextState.DisposeAsync().ConfigureAwait(false);
                else if (createdStreamSession is not null)
                    await createdStreamSession.DisposeAsync().ConfigureAwait(false);

                if (createdRoute is not null)
                {
                    audioRoutes.TryRemove(createdRoute.DeviceId, out _);
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
        AudioRoute route,
        IVocoderBackend? activeVocoder,
        double gain,
        double balance)
    {
        IAudioPlayback? playback = null;
        IVocoderSession? vocoderSession = null;
        ChannelReceiveAudioSession? session = null;
        var sampleContext = new ReceiveSampleContext();
        try
        {
            IAudioPlayback mixerPlayback = route.Mixer.OpenChannel(
                $"{channel.Definition.SystemName}/{channel.Name}");
            playback = samplesObserver is null && presentationSamplesObserver is null
                ? mixerPlayback
                : new ObservedAudioPlayback(
                    mixerPlayback,
                    samples =>
                    {
                        if (sampleContext.TryGet(out uint streamId, out uint sourceId))
                            samplesObserver?.Invoke(channel, streamId, sourceId, samples);
                    },
                    presentationSamplesObserver is null
                        ? null
                        : (samples, delay) =>
                        {
                            if (sampleContext.TryGetLatestStream(out uint streamId))
                            {
                                presentationSamplesObserver(
                                    channel,
                                    streamId,
                                    samples,
                                    delay);
                            }
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

            session = new ChannelReceiveAudioSession(
                channel.Definition,
                vocoderSession,
                playback,
                p25KeyResolver,
                dmrKeyResolver,
                nxdnKeyResolver);
            session.SetGain(gain);
            session.SetBalance(balance);
            vocoderSession = null;
            playback = null;
            return new StreamSessionState(session, sampleContext);
        }
        catch
        {
            if (session is not null)
                await session.DisposeAsync().ConfigureAwait(false);
            else
            {
                vocoderSession?.Dispose();
                if (playback is not null)
                    await playback.DisposeAsync().ConfigureAwait(false);
            }
            throw;
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

        if (!sessions.TryGetValue(channel, out SessionState? state) || !state.TryAcquire())
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
                sessionRoutes.TryRemove(channel, out string? routeId);
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
                    audioRoutes.TryRemove(routeId, out AudioRoute? route))
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
            if (sessions.TryGetValue(channel, out SessionState? state))
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
        lock (playbackPolicySync)
        {
            if (!audioRoutes.TryAdd(route.DeviceId, route))
                throw new InvalidOperationException("The receive output route was added concurrently.");
            if (livePlaybackDiscarded)
                route.Mixer.SetInputDiscarded(discarded: true);
        }
        createdRoute = route;
        return route;
    }

    private sealed record AudioRoute(string DeviceId, IAudioBackend Backend, AudioMixer Mixer);

    private sealed class SessionState : IAsyncDisposable
    {
        private const int MaximumStreamSessions = 8;
        private static readonly TimeSpan CompletedStreamRetention = TimeSpan.FromSeconds(1);
        private readonly object sync = new();
        private readonly object streamSync = new();
        private readonly TaskCompletionSource idle =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<uint, StreamSessionState> streams = [];
        private readonly ReceiveStreamLifecycle receiveLifecycle =
            ReceiveStreamLifecycle.CreateDefault();
        private readonly Func<ValueTask<StreamSessionState>> createStreamSession;
        private StreamSessionState? unboundStream;
        private ReceiveAudioDiagnostics completedDiagnostics = new(0, 0, 0, 0);
        private double gain;
        private double balance;
        private bool livePlaybackEnabled;
        private int operations;
        private bool stopping;

        public SessionState(
            StreamSessionState initialStream,
            Func<ValueTask<StreamSessionState>> createStreamSession,
            double gain,
            double balance,
            bool livePlaybackEnabled)
        {
            unboundStream = initialStream ?? throw new ArgumentNullException(nameof(initialStream));
            this.createStreamSession = createStreamSession ?? throw new ArgumentNullException(nameof(createStreamSession));
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
            foreach (StreamSessionState stream in oldStreams)
                await stream.DisposeAsync().ConfigureAwait(false);
            ProcessGate.Dispose();
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

    private sealed class StreamSessionState(
        ChannelReceiveAudioSession session,
        ReceiveSampleContext sampleContext) : IAsyncDisposable
    {
        public ChannelReceiveAudioSession Session { get; } = session;
        public ReceiveSampleContext SampleContext { get; } = sampleContext;
        public uint StreamId { get; set; }
        public DateTimeOffset LastActivity { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? CompletedAt { get; set; }

        public ValueTask DisposeAsync() => Session.DisposeAsync();
    }

    private sealed class ReceiveSampleContext
    {
        private uint streamId;
        private uint sourceId;
        private uint latestStreamId;

        public void Set(uint nextStreamId, uint nextSourceId)
        {
            streamId = nextStreamId;
            sourceId = nextSourceId;
            latestStreamId = nextStreamId;
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

        public bool TryGetLatestStream(out uint currentStreamId)
        {
            currentStreamId = latestStreamId;
            return currentStreamId != 0;
        }
    }

    private sealed class ObservedAudioPlayback :
        IAudioPlayback,
        IConcealmentAudioPlayback,
        ILiveAudioPlaybackControl,
        IAudioGainControl,
        IAudioBalanceControl
    {
        private readonly IAudioPlayback inner;
        private readonly ILiveAudioPlaybackControl livePlaybackControl;
        private readonly Action<ReadOnlyMemory<short>> observer;

        public ObservedAudioPlayback(
            IAudioPlayback inner,
            Action<ReadOnlyMemory<short>> observer,
            Action<ReadOnlyMemory<short>, TimeSpan>? presentationObserver)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            livePlaybackControl = inner as ILiveAudioPlaybackControl ??
                throw new ArgumentException(
                    "Observed receive playback requires independent live-presentation control.",
                    nameof(inner));
            this.observer = observer ?? throw new ArgumentNullException(nameof(observer));
            if (presentationObserver is not null &&
                inner is IAudioPlaybackPresentationSource presentationSource)
            {
                presentationSource.SetPresentationObserver(presentationObserver);
            }
        }

        public PcmAudioFormat Format => inner.Format;

        public bool LivePlaybackEnabled
        {
            get => livePlaybackControl.LivePlaybackEnabled;
            set => livePlaybackControl.LivePlaybackEnabled = value;
        }

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

        public async ValueTask WriteConcealmentAsync(
            ReadOnlyMemory<short> samples,
            CancellationToken cancellationToken = default)
        {
            if (inner is IConcealmentAudioPlayback concealmentPlayback)
            {
                await concealmentPlayback.WriteConcealmentAsync(samples, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await inner.WriteAsync(samples, cancellationToken).ConfigureAwait(false);
            }
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
