using DvmConsole.Audio;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Vocoder;

namespace DvmConsole.Desktop;

// Decodes enabled patch-source channels without opening a local playback
// device. The resulting PCM is handed to the patch router; normal Listen
// remains independently responsible for operator playback and recording.
public sealed class PatchSourceDecodeCoordinator : IAsyncDisposable
{
    private readonly SemaphoreSlim configurationGate = new(1, 1);
    private readonly IP25KeyResolver? p25KeyResolver;
    private readonly IDmrKeyResolver? dmrKeyResolver;
    private readonly INxdnKeyResolver? nxdnKeyResolver;
    private readonly Action<ChannelViewModel, uint, uint, ReadOnlyMemory<short>> observer;
    private readonly Func<IVocoderBackend> createVocoderBackend;
    private readonly object sync = new();
    private readonly Dictionary<ChannelViewModel, SessionState> sessions = [];
    private IVocoderBackend? vocoderBackend;
    private Task? disposeTask;
    private long requestedConfigurationRevision;
    private bool disposed;

    public PatchSourceDecodeCoordinator(
        IP25KeyResolver? p25KeyResolver,
        Action<ChannelViewModel, ReadOnlyMemory<short>> observer,
        Func<IVocoderBackend>? createVocoderBackend = null,
        IDmrKeyResolver? dmrKeyResolver = null,
        INxdnKeyResolver? nxdnKeyResolver = null)
        : this(
            p25KeyResolver,
            (channel, _, _, samples) => observer(channel, samples),
            createVocoderBackend,
            dmrKeyResolver,
            nxdnKeyResolver)
    {
        ArgumentNullException.ThrowIfNull(observer);
    }

    public PatchSourceDecodeCoordinator(
        IP25KeyResolver? p25KeyResolver,
        Action<ChannelViewModel, uint, uint, ReadOnlyMemory<short>> observer,
        Func<IVocoderBackend>? createVocoderBackend = null,
        IDmrKeyResolver? dmrKeyResolver = null,
        INxdnKeyResolver? nxdnKeyResolver = null)
    {
        this.p25KeyResolver = p25KeyResolver;
        this.dmrKeyResolver = dmrKeyResolver;
        this.nxdnKeyResolver = nxdnKeyResolver;
        this.observer = observer ?? throw new ArgumentNullException(nameof(observer));
        this.createVocoderBackend = createVocoderBackend ??
            (() => new SoftwareVocoderBackend());
    }

    public bool IsActive(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        lock (sync)
            return sessions.ContainsKey(channel);
    }

    public bool IsTrackingStream(ChannelViewModel channel, uint streamId)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (streamId == 0)
            return false;
        lock (sync)
        {
            return sessions.TryGetValue(channel, out SessionState? state) &&
                state.ActiveStreamId == streamId;
        }
    }

    public IReadOnlyList<ChannelViewModel> ActiveChannels
    {
        get
        {
            lock (sync)
                return sessions.Keys.ToArray();
        }
    }

    public async Task ApplyChannelsAsync(
        IEnumerable<ChannelViewModel> channels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ObjectDisposedException.ThrowIf(disposed, this);

        ChannelViewModel[] requested = channels
            .Where(channel => channel is not null &&
                (channel.Definition.Mode is "dmr" or "p25" or "nxdn" or "analog"))
            .Distinct()
            .ToArray();
        long revision = Interlocked.Increment(ref requestedConfigurationRevision);
        await configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (revision != Volatile.Read(ref requestedConfigurationRevision))
                return;

            HashSet<ChannelViewModel> requestedSet = requested.ToHashSet();
            ChannelViewModel[] removedChannels;
            lock (sync)
            {
                removedChannels = sessions.Keys
                    .Where(channel => !requestedSet.Contains(channel))
                    .ToArray();
            }

            foreach (ChannelViewModel channel in removedChannels)
            {
                SessionState? state;
                lock (sync)
                    sessions.Remove(channel, out state);
                if (state is not null)
                    await state.DisposeAsync().ConfigureAwait(false);
            }

            foreach (ChannelViewModel channel in requested)
            {
                lock (sync)
                {
                    if (sessions.ContainsKey(channel))
                        continue;
                }

                if (!CanDecode(channel))
                    continue;

                ChannelReceiveAudioSession? session = null;
                IVocoderSession? createdVocoderSession = null;
                var sampleContext = new ReceiveSampleContext();
                try
                {
                    if (channel.Definition.Mode is "dmr" or "p25" or "nxdn")
                    {
                        vocoderBackend ??= createVocoderBackend();
                        createdVocoderSession = vocoderBackend.CreateSession(
                            channel.Definition.Mode == "dmr"
                                ? VocoderMode.DmrAmbe
                                : channel.Definition.Mode == "nxdn"
                                    ? VocoderMode.NxdnAmbe
                                : VocoderMode.P25Imbe);
                    }

                    session = new ChannelReceiveAudioSession(
                        channel.Definition,
                        createdVocoderSession,
                        new ObservedDiscardPlayback(
                            PcmAudioFormat.Voice8KhzMono16Bit,
                            samples =>
                            {
                                if (sampleContext.TryGet(out uint streamId, out uint sourceId))
                                    observer(channel, streamId, sourceId, samples);
                            }),
                        p25KeyResolver,
                        dmrKeyResolver,
                        nxdnKeyResolver,
                        channel);
                    createdVocoderSession = null;
                    lock (sync)
                        sessions.Add(channel, new SessionState(session, sampleContext));
                }
                catch
                {
                    if (session is not null)
                        await session.DisposeAsync().ConfigureAwait(false);
                    createdVocoderSession?.Dispose();
                    throw;
                }
            }

            bool hasSessions;
            lock (sync)
                hasSessions = sessions.Count > 0;
            if (!hasSessions)
            {
                vocoderBackend?.Dispose();
                vocoderBackend = null;
            }
        }
        finally
        {
            configurationGate.Release();
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

        SessionState? state;
        lock (sync)
            sessions.TryGetValue(channel, out state);
        if (state is null)
            return 0;

        return await state.ProcessAsync(traffic, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        long revision = Interlocked.Increment(ref requestedConfigurationRevision);
        await configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (revision != Volatile.Read(ref requestedConfigurationRevision))
                return;

            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            configurationGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (sync)
            return new ValueTask(disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Increment(ref requestedConfigurationRevision);
        await configurationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
                return;
            await StopCoreAsync().ConfigureAwait(false);
            disposed = true;
        }
        finally
        {
            configurationGate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        SessionState[] activeSessions;
        lock (sync)
        {
            activeSessions = sessions.Values.ToArray();
            sessions.Clear();
        }

        foreach (SessionState state in activeSessions)
            await state.DisposeAsync().ConfigureAwait(false);
        vocoderBackend?.Dispose();
        vocoderBackend = null;
    }

    private bool CanDecode(ChannelViewModel channel)
    {
        if (!channel.RequireEncryptedTraffic)
            return true;
        return channel.Definition.Mode switch
        {
            "p25" => p25KeyResolver?.CanResolve(
                channel.Definition.SystemName,
                channel.Definition.EncryptionAlgorithm,
                channel.Definition.EncryptionKeyId) == true,
            "dmr" => dmrKeyResolver?.CanResolve(
                channel.Definition.SystemName,
                channel.Definition.EncryptionAlgorithm,
                channel.Definition.EncryptionKeyId) == true,
            "nxdn" => nxdnKeyResolver?.CanResolve(
                channel.Definition.SystemName,
                channel.Definition.EncryptionAlgorithm,
                channel.Definition.EncryptionKeyId) == true,
            _ => false
        };
    }

    private sealed class ObservedDiscardPlayback(
        PcmAudioFormat format,
        Action<ReadOnlyMemory<short>> observer) : IAudioPlayback
    {
        public PcmAudioFormat Format { get; } = format;

        public ValueTask WriteAsync(ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            observer(samples);
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SessionState(
        ChannelReceiveAudioSession session,
        ReceiveSampleContext sampleContext) : IAsyncDisposable
    {
        private readonly SemaphoreSlim processingGate = new(1, 1);
        private readonly object streamSync = new();
        private bool disposed;
        private uint activeStreamId;

        public ChannelReceiveAudioSession Session { get; } = session;
        public ReceiveSampleContext SampleContext { get; } = sampleContext;
        public uint ActiveStreamId
        {
            get
            {
                lock (streamSync)
                    return activeStreamId;
            }
        }

        public async Task<int> ProcessAsync(
            FneTrafficFrame traffic,
            CancellationToken cancellationToken)
        {
            await processingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (disposed)
                    return 0;

                bool terminator = ReceiveTrafficClassifier.IsTerminator(traffic);
                if (!terminator &&
                    (ReceiveTrafficClassifier.CarriesVoicePayload(traffic) ||
                     ReceiveTrafficClassifier.IsDefinitiveStart(traffic)))
                {
                    lock (streamSync)
                        activeStreamId = traffic.StreamId;
                }

                SampleContext.Set(traffic.StreamId, traffic.SourceId);
                try
                {
                    return await Session.ProcessAsync(traffic, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    SampleContext.Clear();
                    if (terminator)
                    {
                        lock (streamSync)
                        {
                            if (activeStreamId == traffic.StreamId)
                                activeStreamId = 0;
                        }
                    }
                }
            }
            finally
            {
                processingGate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await processingGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (disposed)
                    return;
                await Session.DisposeAsync().ConfigureAwait(false);
                disposed = true;
            }
            finally
            {
                processingGate.Release();
            }
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
}
