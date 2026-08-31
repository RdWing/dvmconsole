using DvmConsole.Audio;
using DvmConsole.Core.Runtime;
using DvmConsole.Media;
using DvmConsole.Vocoder;

namespace DvmConsole.Application;

// Decodes enabled patch-source channels without opening a local playback
// device. The resulting PCM is handed to the patch router; normal Listen
// remains independently responsible for operator playback and recording.
public sealed class PatchSourceDecodeCoordinator : IAsyncDisposable
{
    private readonly SemaphoreSlim configurationGate = new(1, 1);
    private readonly IP25KeyResolver? p25KeyResolver;
    private readonly IDmrKeyResolver? dmrKeyResolver;
    private readonly INxdnKeyResolver? nxdnKeyResolver;
    private readonly Action<ChannelId, uint, uint, ReadOnlyMemory<short>> observer;
    private readonly Func<IVocoderBackend> createVocoderBackend;
    private readonly object sync = new();
    private readonly Dictionary<ChannelId, SessionState> sessions = [];
    private ChannelId[] activeChannels = [];
    private IVocoderBackend? vocoderBackend;
    private Task? disposeTask;
    private long requestedConfigurationRevision;
    private bool disposed;

    public PatchSourceDecodeCoordinator(
        IP25KeyResolver? p25KeyResolver,
        Action<ChannelId, ReadOnlyMemory<short>> observer,
        Func<IVocoderBackend> createVocoderBackend,
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
        Action<ChannelId, uint, uint, ReadOnlyMemory<short>> observer,
        Func<IVocoderBackend> createVocoderBackend,
        IDmrKeyResolver? dmrKeyResolver = null,
        INxdnKeyResolver? nxdnKeyResolver = null)
    {
        this.p25KeyResolver = p25KeyResolver;
        this.dmrKeyResolver = dmrKeyResolver;
        this.nxdnKeyResolver = nxdnKeyResolver;
        this.observer = observer ?? throw new ArgumentNullException(nameof(observer));
        this.createVocoderBackend = createVocoderBackend ??
            throw new ArgumentNullException(nameof(createVocoderBackend));
    }

    public bool IsActive(ChannelId channelId)
    {
        lock (sync)
            return sessions.ContainsKey(channelId);
    }

    public bool IsTrackingStream(ChannelId channelId, uint streamId)
    {
        if (streamId == 0)
            return false;
        lock (sync)
        {
            return sessions.TryGetValue(channelId, out SessionState? state) &&
                state.ActiveStreamId == streamId;
        }
    }

    public IReadOnlyList<ChannelId> ActiveChannels
        => Volatile.Read(ref activeChannels);

    public async Task ApplyChannelsAsync(
        IEnumerable<ReceiveChannelDescriptor> channels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ObjectDisposedException.ThrowIf(disposed, this);

        ReceiveChannelDescriptor[] requested = channels
            .Where(channel => channel is not null)
            .GroupBy(channel => channel.Id)
            .Select(group => group.First())
            .ToArray();
        long revision = Interlocked.Increment(ref requestedConfigurationRevision);
        await configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (revision != Volatile.Read(ref requestedConfigurationRevision))
                return;

            HashSet<ChannelId> requestedSet = requested.Select(channel => channel.Id).ToHashSet();
            ChannelId[] removedChannels;
            lock (sync)
            {
                removedChannels = sessions.Keys
                    .Where(channel => !requestedSet.Contains(channel))
                    .ToArray();
            }

            foreach (ChannelId channelId in removedChannels)
            {
                SessionState? state;
                lock (sync)
                {
                    sessions.Remove(channelId, out state);
                    PublishActiveChannels();
                }
                if (state is not null)
                    await state.DisposeAsync().ConfigureAwait(false);
            }

            foreach (ReceiveChannelDescriptor channel in requested)
            {
                lock (sync)
                {
                    if (sessions.ContainsKey(channel.Id))
                        continue;
                }

                ChannelReceiveAudioSession? session = null;
                IVocoderSession? createdVocoderSession = null;
                var sampleContext = new ReceiveSampleContext();
                try
                {
                    if (ChannelVocoderPolicy.RequiresVocoder(channel.Definition.Protocol))
                    {
                        vocoderBackend ??= createVocoderBackend();
                        createdVocoderSession = vocoderBackend.CreateSession(
                            ChannelVocoderPolicy.ToVocoderMode(channel.Definition.Protocol));
                    }

                    session = new ChannelReceiveAudioSession(
                        channel.Definition,
                        createdVocoderSession,
                        new ObservedDiscardPlayback(
                            PcmAudioFormat.Voice8KhzMono16Bit,
                            samples =>
                            {
                                if (sampleContext.TryGet(out uint streamId, out uint sourceId))
                                    observer(channel.Id, streamId, sourceId, samples);
                            }),
                        p25KeyResolver,
                        dmrKeyResolver,
                        nxdnKeyResolver);
                    createdVocoderSession = null;
                    lock (sync)
                    {
                        sessions.Add(channel.Id, new SessionState(session, sampleContext));
                        PublishActiveChannels();
                    }
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
        ChannelId channelId,
        IRadioMediaFrame traffic,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(traffic);
        ObjectDisposedException.ThrowIf(disposed, this);

        SessionState? state;
        lock (sync)
            sessions.TryGetValue(channelId, out state);
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
            PublishActiveChannels();
        }

        foreach (SessionState state in activeSessions)
            await state.DisposeAsync().ConfigureAwait(false);
        vocoderBackend?.Dispose();
        vocoderBackend = null;
    }

    // Called only while sync is held. Readers receive an immutable snapshot
    // without taking the lock or allocating on every received packet.
    private void PublishActiveChannels()
        => Volatile.Write(ref activeChannels, sessions.Keys.ToArray());

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
            IRadioMediaFrame traffic,
            CancellationToken cancellationToken)
        {
            await processingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (disposed)
                    return 0;

                bool terminator = RadioReceiveTrafficClassifier.IsTerminator(traffic);
                if (!terminator &&
                    (RadioReceiveTrafficClassifier.CarriesVoicePayload(traffic) ||
                     RadioReceiveTrafficClassifier.IsDefinitiveStart(traffic)))
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
