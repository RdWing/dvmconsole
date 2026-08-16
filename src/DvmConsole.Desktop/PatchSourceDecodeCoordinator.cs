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
    private readonly Action<ChannelViewModel, ReadOnlyMemory<short>> observer;
    private readonly Func<IVocoderBackend> createVocoderBackend;
    private readonly object sync = new();
    private readonly Dictionary<ChannelViewModel, ChannelReceiveAudioSession> sessions = [];
    private IVocoderBackend? vocoderBackend;
    private bool disposed;

    public PatchSourceDecodeCoordinator(
        IP25KeyResolver? p25KeyResolver,
        Action<ChannelViewModel, ReadOnlyMemory<short>> observer,
        Func<IVocoderBackend>? createVocoderBackend = null)
    {
        this.p25KeyResolver = p25KeyResolver;
        this.observer = observer ?? throw new ArgumentNullException(nameof(observer));
        this.createVocoderBackend = createVocoderBackend ??
            (() => new SoftwareVocoderBackend(Environment.GetEnvironmentVariable("DVMVOCODER_LIBRARY")));
    }

    public bool IsActive(ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        lock (sessions)
            return sessions.ContainsKey(channel);
    }

    public async Task ApplyChannelsAsync(
        IEnumerable<ChannelViewModel> channels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ObjectDisposedException.ThrowIf(disposed, this);

        ChannelViewModel[] requested = channels
            .Where(channel => channel is not null &&
                (channel.Definition.Mode is "dmr" or "p25" or "analog"))
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
                ChannelReceiveAudioSession? session;
                lock (sync)
                    sessions.Remove(channel, out session);
                if (session is not null)
                    await session.DisposeAsync().ConfigureAwait(false);
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
                try
                {
                    if (channel.Definition.Mode is "dmr" or "p25")
                    {
                        vocoderBackend ??= createVocoderBackend();
                        createdVocoderSession = vocoderBackend.CreateSession(
                            channel.Definition.Mode == "dmr"
                                ? VocoderMode.DmrAmbe
                                : VocoderMode.P25Imbe);
                    }

                    session = new ChannelReceiveAudioSession(
                        channel.Definition,
                        createdVocoderSession,
                        new ObservedDiscardPlayback(
                            PcmAudioFormat.Voice8KhzMono16Bit,
                            samples => observer(channel, samples)),
                        p25KeyResolver);
                    createdVocoderSession = null;
                    lock (sync)
                        sessions.Add(channel, session);
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
            ChannelReceiveAudioSession? session;
            lock (sync)
                sessions.TryGetValue(channel, out session);
            return session is not null
                ? await session.ProcessAsync(traffic, cancellationToken).ConfigureAwait(false)
                : 0;
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
        ChannelReceiveAudioSession[] activeSessions;
        lock (sync)
        {
            activeSessions = sessions.Values.ToArray();
            sessions.Clear();
        }

        foreach (ChannelReceiveAudioSession session in activeSessions)
            await session.DisposeAsync().ConfigureAwait(false);
        vocoderBackend?.Dispose();
        vocoderBackend = null;
    }

    private bool CanDecode(ChannelViewModel channel)
    {
        if (channel.Definition.Mode == "nxdn")
            return false;
        return !channel.Definition.IsEncrypted ||
            (channel.Definition.Mode == "p25" &&
             p25KeyResolver is not null &&
             p25KeyResolver.CanResolve(
                 channel.Definition.EncryptionAlgorithm,
                 channel.Definition.EncryptionKeyId));
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
}
