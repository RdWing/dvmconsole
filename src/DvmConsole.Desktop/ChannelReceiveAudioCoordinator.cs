using DvmConsole.Audio;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Vocoder;

namespace DvmConsole.Desktop;

/// <summary>
/// Owns explicitly selected receive-audio channels. Clear DMR/P25/analog sessions
/// share one output stream through a fixed-rate PCM mixer, and the coordinator
/// serializes traffic processing so decoded PCM frames remain ordered per
/// channel before mixing.
/// Audio devices and the native vocoder are created only when Listen is used.
/// </summary>
public sealed class ChannelReceiveAudioCoordinator : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<ChannelViewModel, ChannelReceiveAudioSession> sessions = [];
    private readonly Dictionary<ChannelViewModel, string> sessionRoutes = [];
    private readonly Dictionary<string, AudioRoute> audioRoutes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<IAudioBackend> createAudioBackend;
    private readonly Func<IVocoderBackend> createVocoderBackend;
    private readonly IP25KeyResolver? p25KeyResolver;
    private readonly Action<ChannelViewModel, ReadOnlyMemory<short>>? samplesObserver;
    private readonly Func<ChannelViewModel, double>? getChannelGain;
    private readonly Func<ChannelViewModel, string?>? getOutputDeviceId;
    private readonly Func<INxdnVocoderBackend?>? createNxdnVocoderBackend;
    private volatile ChannelViewModel[] activeChannels = [];
    private IAudioBackend? audioBackend;
    private IVocoderBackend? vocoderBackend;
    private INxdnVocoderBackend? nxdnVocoderBackend;
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
        Action<ChannelViewModel, ReadOnlyMemory<short>>? samplesObserver,
        Func<ChannelViewModel, double>? getChannelGain = null,
        Func<ChannelViewModel, string?>? getOutputDeviceId = null,
        Func<INxdnVocoderBackend?>? createNxdnVocoderBackend = null)
        : this(
            () => AudioBackendFactory.CreateDefault(Environment.GetEnvironmentVariable("DVM_AUDIO_LIBRARY")),
            () => new SoftwareVocoderBackend(Environment.GetEnvironmentVariable("DVMVOCODER_LIBRARY")),
            p25KeyResolver,
            samplesObserver,
            getChannelGain,
            getOutputDeviceId,
            createNxdnVocoderBackend)
    {
    }

    public ChannelReceiveAudioCoordinator(
        Func<IAudioBackend> createAudioBackend,
        Func<IVocoderBackend> createVocoderBackend,
        IP25KeyResolver? p25KeyResolver = null,
        Action<ChannelViewModel, ReadOnlyMemory<short>>? samplesObserver = null,
        Func<ChannelViewModel, double>? getChannelGain = null,
        Func<ChannelViewModel, string?>? getOutputDeviceId = null,
        Func<INxdnVocoderBackend?>? createNxdnVocoderBackend = null)
    {
        this.createAudioBackend = createAudioBackend ?? throw new ArgumentNullException(nameof(createAudioBackend));
        this.createVocoderBackend = createVocoderBackend ?? throw new ArgumentNullException(nameof(createVocoderBackend));
        this.p25KeyResolver = p25KeyResolver;
        this.samplesObserver = samplesObserver;
        this.getChannelGain = getChannelGain;
        this.getOutputDeviceId = getOutputDeviceId;
        this.createNxdnVocoderBackend = createNxdnVocoderBackend;
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
        return sessions.TryGetValue(channel, out ChannelReceiveAudioSession? session)
            ? session.GetDiagnostics()
            : new ReceiveAudioDiagnostics(0, 0, 0, 0);
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
            if (channel.Definition.Mode == "nxdn" && createNxdnVocoderBackend is null)
                throw new NotSupportedException(
                    "NXDN receive audio requires an injected FEC/AMBE+2 decoder.");
            if (channel.Definition.IsEncrypted &&
                (channel.Definition.Mode != "p25" ||
                 p25KeyResolver is null ||
                 !p25KeyResolver.CanResolve(
                     channel.Definition.EncryptionAlgorithm,
                     channel.Definition.EncryptionKeyId)))
            {
                throw new NotSupportedException(
                    "Encrypted receive audio requires a configured P25 key; this channel cannot be opened safely.");
            }

            IAudioBackend? createdAudio = null;
            IVocoderBackend? createdVocoder = null;
            AudioRoute? createdRoute = null;
            IAudioPlayback? createdChannelPlayback = null;
            IVocoderSession? vocoderSession = null;
            INxdnVocoderSession? nxdnVocoderSession = null;
            INxdnVocoderBackend? createdNxdnVocoder = null;
            ChannelReceiveAudioSession? nextSession = null;

            try
            {
                INxdnVocoderBackend? activeNxdnVocoder = channel.Definition.Mode == "nxdn"
                    ? nxdnVocoderBackend ??= createdNxdnVocoder = createNxdnVocoderBackend!()
                    : null;
                if (channel.Definition.Mode == "nxdn" &&
                    (activeNxdnVocoder is null || !activeNxdnVocoder.IsAvailable))
                {
                    throw new NotSupportedException(
                        "NXDN receive audio requires an available FEC/AMBE+2 decoder.");
                }

                IAudioBackend activeAudio = audioBackend ??= createdAudio = createAudioBackend();
                IVocoderBackend? activeVocoder = channel.Definition.Mode is "dmr" or "p25"
                    ? vocoderBackend ??= createdVocoder = createVocoderBackend()
                    : null;
                AudioRoute activeRoute = GetOrCreateRoute(
                    activeAudio,
                    getOutputDeviceId?.Invoke(channel),
                    out createdRoute);
                IAudioPlayback mixerChannelPlayback = activeRoute.Mixer.OpenChannel();
                createdChannelPlayback = samplesObserver is null
                    ? mixerChannelPlayback
                    : new ObservedAudioPlayback(
                        mixerChannelPlayback,
                        samples => samplesObserver(channel, samples));

                if (activeVocoder is not null)
                {
                    VocoderMode mode = channel.Definition.Mode == "dmr"
                        ? VocoderMode.DmrAmbe
                        : VocoderMode.P25Imbe;
                    vocoderSession = activeVocoder.CreateSession(mode);
                }
                if (activeNxdnVocoder is not null)
                    nxdnVocoderSession = activeNxdnVocoder.CreateSession();
                nextSession = new ChannelReceiveAudioSession(
                    channel.Definition,
                    vocoderSession,
                    createdChannelPlayback,
                    p25KeyResolver,
                    nxdnVocoderSession);
                nextSession.SetGain(getChannelGain?.Invoke(channel) ?? 1.0);
                vocoderSession = null;
                nxdnVocoderSession = null;
                createdChannelPlayback = null;

                sessions.Add(channel, nextSession);
                sessionRoutes.Add(channel, activeRoute.DeviceId);
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
                    nxdnVocoderSession?.Dispose();
                }

                if (createdRoute is not null)
                {
                    audioRoutes.Remove(createdRoute.DeviceId);
                    await createdRoute.Mixer.DisposeAsync().ConfigureAwait(false);
                }
                createdVocoder?.Dispose();
                createdAudio?.Dispose();
                createdNxdnVocoder?.Dispose();
                if (createdAudio is not null)
                    audioBackend = null;
                if (createdVocoder is not null)
                    vocoderBackend = null;
                if (createdNxdnVocoder is not null)
                    nxdnVocoderBackend = null;

                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<int> ProcessAsync(
        ChannelViewModel channel,
        FneTrafficFrame traffic,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(traffic);
        ObjectDisposedException.ThrowIf(disposed, this);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return sessions.TryGetValue(channel, out ChannelReceiveAudioSession? session)
                ? await session.ProcessAsync(traffic, cancellationToken).ConfigureAwait(false)
                : 0;
        }
        finally
        {
            gate.Release();
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
            if (sessions.Remove(channel, out ChannelReceiveAudioSession? session))
            {
                sessionRoutes.Remove(channel, out string? routeId);
                activeChannels = sessions.Keys.ToArray();
                try
                {
                    await session.DisposeAsync().ConfigureAwait(false);
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
                        await route.Mixer.DisposeAsync().ConfigureAwait(false);
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
            if (sessions.TryGetValue(channel, out ChannelReceiveAudioSession? session))
                session.SetGain(gain);
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
        }
    }

    private async Task StopCoreAsync()
    {
        ChannelReceiveAudioSession[] oldSessions = sessions.Values.ToArray();
        sessions.Clear();
        sessionRoutes.Clear();
        activeChannels = [];

        Exception? failure = null;
        foreach (ChannelReceiveAudioSession session in oldSessions)
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
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
        INxdnVocoderBackend? oldNxdnVocoder = nxdnVocoderBackend;
        IAudioBackend? oldAudio = audioBackend;
        AudioRoute[] oldRoutes = audioRoutes.Values.ToArray();
        audioRoutes.Clear();
        vocoderBackend = null;
        nxdnVocoderBackend = null;
        audioBackend = null;

        Exception? failure = null;
        foreach (AudioRoute route in oldRoutes)
        {
            try
            {
                await route.Mixer.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }

        oldVocoder?.Dispose();
        oldNxdnVocoder?.Dispose();
        oldAudio?.Dispose();

        if (failure is not null)
            throw failure;
    }

    private AudioRoute GetOrCreateRoute(
        IAudioBackend backend,
        string? requestedDeviceId,
        out AudioRoute? createdRoute)
    {
        IReadOnlyList<AudioDeviceInfo> devices = backend.EnumerateDevices(AudioDirection.Output);
        AudioDeviceInfo output = devices
            .FirstOrDefault(device => !string.IsNullOrWhiteSpace(requestedDeviceId) &&
                                      device.Id.Equals(requestedDeviceId, StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault(device => device.IsDefault)
            ?? devices.FirstOrDefault()
            ?? throw new InvalidOperationException("No audio output device is available.");

        if (audioRoutes.TryGetValue(output.Id, out AudioRoute? existingRoute))
        {
            createdRoute = null;
            return existingRoute;
        }

        IAudioPlayback playback = backend.OpenPlayback(output, PcmAudioFormat.Voice8KhzMono16Bit);
        var route = new AudioRoute(output.Id, new AudioMixer(playback));
        audioRoutes.Add(route.DeviceId, route);
        createdRoute = route;
        return route;
    }

    private sealed record AudioRoute(string DeviceId, AudioMixer Mixer);

    private sealed class ObservedAudioPlayback(
        IAudioPlayback inner,
        Action<ReadOnlyMemory<short>> observer) : IAudioPlayback, IAudioGainControl
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
