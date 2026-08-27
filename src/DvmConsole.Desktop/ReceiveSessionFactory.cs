using DvmConsole.Audio;
using DvmConsole.Core.Runtime;
using DvmConsole.Media;
using DvmConsole.Vocoder;

namespace DvmConsole.Desktop;

internal sealed class ReceiveSessionFactory
{
    private readonly IP25KeyResolver? p25KeyResolver;
    private readonly IDmrKeyResolver? dmrKeyResolver;
    private readonly INxdnKeyResolver? nxdnKeyResolver;
    private readonly Action<ChannelViewModel, uint, uint, ReadOnlyMemory<short>>? samplesObserver;

    public ReceiveSessionFactory(
        IP25KeyResolver? p25KeyResolver,
        IDmrKeyResolver? dmrKeyResolver,
        INxdnKeyResolver? nxdnKeyResolver,
        Action<ChannelViewModel, uint, uint, ReadOnlyMemory<short>>? samplesObserver)
    {
        this.p25KeyResolver = p25KeyResolver;
        this.dmrKeyResolver = dmrKeyResolver;
        this.nxdnKeyResolver = nxdnKeyResolver;
        this.samplesObserver = samplesObserver;
    }

    public bool CanResolveEncryption(ChannelRuntimeDefinition definition)
        => definition.Mode switch
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

    public async ValueTask<StreamSessionState> CreateAsync(
        ChannelViewModel channel,
        ReceiveEpisodePlaybackPool playbackPool,
        IVocoderBackend? activeVocoder,
        double gain,
        double balance)
    {
        IAudioPlayback? playback = null;
        IVocoderSession? vocoderSession = null;
        ChannelReceiveAudioSession? session = null;
        ReceiveEpisodePlaybackPool.DeferredEpisodePlayback? episodePlayback = null;
        var sampleContext = new ReceiveSampleContext();
        try
        {
            episodePlayback = playbackPool.CreatePlayback();
            playback = samplesObserver is null
                ? episodePlayback
                : new ObservedAudioPlayback(
                    episodePlayback,
                    samples =>
                    {
                        if (sampleContext.TryGet(out uint streamId, out uint sourceId))
                            samplesObserver?.Invoke(channel, streamId, sourceId, samples);
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
                nxdnKeyResolver,
                channel);
            session.SetGain(gain);
            session.SetBalance(balance);
            vocoderSession = null;
            playback = null;
            return new StreamSessionState(session, sampleContext, episodePlayback);
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
}

internal sealed class StreamSessionState(
    ChannelReceiveAudioSession session,
    ReceiveSampleContext sampleContext,
    ReceiveEpisodePlaybackPool.DeferredEpisodePlayback episodePlayback) : IAsyncDisposable
{
    public ChannelReceiveAudioSession Session { get; } = session;
    public ReceiveSampleContext SampleContext { get; } = sampleContext;
    public ReceiveEpisodePlaybackPool.DeferredEpisodePlayback EpisodePlayback { get; } = episodePlayback;
    public uint StreamId { get; set; }
    public DateTimeOffset LastActivity { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public bool? Encrypted { get; set; }

    public ValueTask DisposeAsync() => Session.DisposeAsync();
}

internal sealed class ReceiveSampleContext
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

internal sealed class ObservedAudioPlayback :
    IAudioPlayback,
    IConcealmentAudioPlayback,
    ILivePacketAudioPlayback,
    ILiveAudioPlaybackControl,
    IAudioGainControl,
    IAudioBalanceControl
{
    private readonly IAudioPlayback inner;
    private readonly ILiveAudioPlaybackControl livePlaybackControl;
    private readonly Action<ReadOnlyMemory<short>> observer;

    public ObservedAudioPlayback(
        IAudioPlayback inner,
        Action<ReadOnlyMemory<short>> observer)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        livePlaybackControl = inner as ILiveAudioPlaybackControl ??
            throw new ArgumentException(
                "Observed receive playback requires independent live-presentation control.",
                nameof(inner));
        this.observer = observer ?? throw new ArgumentNullException(nameof(observer));
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

    public async ValueTask WriteAsync(
        ReadOnlyMemory<short> samples,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        observer(samples);
        await inner.WriteAsync(samples, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteConcealmentAsync(
        ReadOnlyMemory<short> samples,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        observer(samples);
        if (inner is IConcealmentAudioPlayback concealmentPlayback)
        {
            await concealmentPlayback.WriteConcealmentAsync(samples, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await inner.WriteAsync(samples, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask WriteLivePacketAsync(
        ReadOnlyMemory<short> samples,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        observer(samples);
        if (inner is ILivePacketAudioPlayback packetPlayback)
        {
            await packetPlayback.WriteLivePacketAsync(samples, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await inner.WriteAsync(samples, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask FlushAsync(CancellationToken cancellationToken = default)
        => inner.FlushAsync(cancellationToken);

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
