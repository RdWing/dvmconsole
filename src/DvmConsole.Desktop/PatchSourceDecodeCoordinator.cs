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
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly IP25KeyResolver? p25KeyResolver;
    private readonly IDmrKeyResolver? dmrKeyResolver;
    private readonly INxdnKeyResolver? nxdnKeyResolver;
    private readonly Action<ChannelViewModel, uint, uint, ReadOnlyMemory<short>> observer;
    private readonly Func<IVocoderBackend> createVocoderBackend;
    private readonly object sync = new();
    private readonly Dictionary<ChannelViewModel, SessionState> sessions = [];
    private IVocoderBackend? vocoderBackend;
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
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
                    await state.Session.DisposeAsync().ConfigureAwait(false);
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
                        nxdnKeyResolver);
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
            SessionState? state;
            lock (sync)
                sessions.TryGetValue(channel, out state);
            if (state is null)
                return 0;

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
            gate.Release();
        }
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
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
        SessionState[] activeSessions;
        lock (sync)
        {
            activeSessions = sessions.Values.ToArray();
            sessions.Clear();
        }

        foreach (SessionState state in activeSessions)
            await state.Session.DisposeAsync().ConfigureAwait(false);
        vocoderBackend?.Dispose();
        vocoderBackend = null;
    }

    private bool CanDecode(ChannelViewModel channel)
    {
        if (!channel.Definition.IsEncrypted)
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

    private sealed record SessionState(
        ChannelReceiveAudioSession Session,
        ReceiveSampleContext SampleContext);

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
