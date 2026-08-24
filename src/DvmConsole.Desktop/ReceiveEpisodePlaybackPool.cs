using DvmConsole.Audio;
using DvmConsole.Media;

namespace DvmConsole.Desktop;

internal readonly record struct ReceivePlaybackEpisode(
    long EpisodeId,
    uint PrimaryStreamId,
    bool RetainUntilEpisodeCompletion);

// Owns one mixer lane per logical receive episode while every physical FNE
// stream keeps its own decoder session. A quiet handoff pauses the lane's live
// clock instead of gap-filling silence; the replacement fragment resumes that
// lane without another mixer startup cushion.
internal sealed class ReceiveEpisodePlaybackPool : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly ChannelViewModel channel;
    private readonly ReceiveAudioRoute route;
    private readonly Action<ChannelViewModel, uint, ReadOnlyMemory<short>, TimeSpan>?
        presentationObserver;
    private readonly Dictionary<long, EpisodeLane> lanes = [];
    private bool disposed;

    public ReceiveEpisodePlaybackPool(
        ChannelViewModel channel,
        ReceiveAudioRoute route,
        Action<ChannelViewModel, uint, ReadOnlyMemory<short>, TimeSpan>? presentationObserver)
    {
        this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
        this.route = route ?? throw new ArgumentNullException(nameof(route));
        this.presentationObserver = presentationObserver;
    }

    public PcmAudioFormat Format => route.Mixer.Format;

    public DeferredEpisodePlayback CreatePlayback()
        => new(this);

    public async ValueTask CompleteEpisodeAsync(long episodeId)
    {
        EpisodeLane? lane;
        lock (sync)
        {
            if (!lanes.Remove(episodeId, out lane))
                return;
            lane.Completed = true;
        }

        await CompleteLaneAsync(lane).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        EpisodeLane[] snapshot;
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            snapshot = lanes.Values.ToArray();
            lanes.Clear();
            foreach (EpisodeLane lane in snapshot)
                lane.Completed = true;
        }

        foreach (EpisodeLane lane in snapshot)
            await CompleteLaneAsync(lane).ConfigureAwait(false);
    }

    private EpisodeLane Acquire(
        ReceivePlaybackEpisode episode,
        double gain,
        double balance,
        bool livePlaybackEnabled)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!lanes.TryGetValue(episode.EpisodeId, out EpisodeLane? lane))
            {
                IAudioPlayback playback = route.Mixer.OpenChannel(
                    $"{channel.Definition.SystemName}/{channel.Name} episode {episode.EpisodeId}");
                lane = new EpisodeLane(
                    episode.EpisodeId,
                    episode.RetainUntilEpisodeCompletion,
                    playback);
                if (playback is IAudioPlaybackPresentationSource presentationSource &&
                    presentationObserver is not null)
                {
                    uint streamId = episode.PrimaryStreamId;
                    presentationSource.SetPresentationObserver((samples, delay) =>
                        presentationObserver(channel, streamId, samples, delay));
                }
                lanes.Add(episode.EpisodeId, lane);
            }

            if (lane.Completed)
                throw new InvalidOperationException("The receive episode playback lane is complete.");
            lane.ActiveLeases++;
            ApplyControls(lane.Playback, gain, balance, livePlaybackEnabled);
            if (lane.Playback is IAudioPlaybackInputExpectationControl expectation)
                expectation.ExpectsMoreInput = livePlaybackEnabled;
            return lane;
        }
    }

    private async ValueTask ReleaseAsync(EpisodeLane lane)
    {
        bool complete;
        lock (sync)
        {
            if (lane.ActiveLeases > 0)
                lane.ActiveLeases--;
            if (lane.ActiveLeases > 0 || lane.Completed)
                return;

            if (lane.Playback is IAudioPlaybackInputExpectationControl expectation)
                expectation.ExpectsMoreInput = false;
            complete = !lane.RetainUntilEpisodeCompletion;
            if (complete)
            {
                lane.Completed = true;
                lanes.Remove(lane.EpisodeId);
            }
        }

        if (complete)
            await CompleteLaneAsync(lane).ConfigureAwait(false);
    }

    private static void ApplyControls(
        IAudioPlayback playback,
        double gain,
        double balance,
        bool livePlaybackEnabled)
    {
        if (playback is IAudioGainControl gainControl)
            gainControl.Gain = gain;
        if (playback is IAudioBalanceControl balanceControl)
            balanceControl.Balance = balance;
        if (playback is ILiveAudioPlaybackControl liveControl)
            liveControl.LivePlaybackEnabled = livePlaybackEnabled;
    }

    private static async ValueTask CompleteLaneAsync(EpisodeLane lane)
    {
        try
        {
            await lane.Playback.DrainAsync().ConfigureAwait(false);
            await lane.Playback.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The route publishes physical-output failures. Episode cleanup
            // must not report the same failed mixer lane a second time.
        }

        try
        {
            await lane.Playback.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // See the route-failure note above.
        }
    }

    private sealed class EpisodeLane(
        long episodeId,
        bool retainUntilEpisodeCompletion,
        IAudioPlayback playback)
    {
        public long EpisodeId { get; } = episodeId;
        public bool RetainUntilEpisodeCompletion { get; } = retainUntilEpisodeCompletion;
        public IAudioPlayback Playback { get; } = playback;
        public int ActiveLeases { get; set; }
        public bool Completed { get; set; }
    }

    internal sealed class DeferredEpisodePlayback :
        IAudioPlayback,
        IConcealmentAudioPlayback,
        ILivePacketAudioPlayback,
        ILiveAudioPlaybackControl,
        IAudioGainControl,
        IAudioBalanceControl
    {
        private readonly ReceiveEpisodePlaybackPool owner;
        private ReceivePlaybackEpisode? episode;
        private EpisodeLane? lane;
        private double gain = 1.0;
        private double balance;
        private bool livePlaybackEnabled = true;
        private bool released;
        private bool disposed;

        public DeferredEpisodePlayback(ReceiveEpisodePlaybackPool owner)
            => this.owner = owner;

        public PcmAudioFormat Format => owner.Format;

        public bool LivePlaybackEnabled
        {
            get => livePlaybackEnabled;
            set
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                livePlaybackEnabled = value;
                if (lane?.Playback is ILiveAudioPlaybackControl control)
                    control.LivePlaybackEnabled = value;
                if (lane?.Playback is IAudioPlaybackInputExpectationControl expectation)
                    expectation.ExpectsMoreInput = value && !released;
            }
        }

        public double Gain
        {
            get => gain;
            set
            {
                gain = value;
                if (lane?.Playback is IAudioGainControl control)
                    control.Gain = value;
            }
        }

        public double Balance
        {
            get => balance;
            set
            {
                balance = value;
                if (lane?.Playback is IAudioBalanceControl control)
                    control.Balance = value;
            }
        }

        public void Bind(ReceivePlaybackEpisode value)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (episode is ReceivePlaybackEpisode current && current != value)
                throw new InvalidOperationException("A physical receive decoder changed logical episodes.");
            episode = value;
        }

        public ValueTask WriteAsync(
            ReadOnlyMemory<short> samples,
            CancellationToken cancellationToken = default)
            => GetLane().Playback.WriteAsync(samples, cancellationToken);

        public ValueTask WriteConcealmentAsync(
            ReadOnlyMemory<short> samples,
            CancellationToken cancellationToken = default)
            => GetLane().Playback is IConcealmentAudioPlayback concealment
                ? concealment.WriteConcealmentAsync(samples, cancellationToken)
                : GetLane().Playback.WriteAsync(samples, cancellationToken);

        public ValueTask WriteLivePacketAsync(
            ReadOnlyMemory<short> samples,
            CancellationToken cancellationToken = default)
            => GetLane().Playback is ILivePacketAudioPlayback packet
                ? packet.WriteLivePacketAsync(samples, cancellationToken)
                : GetLane().Playback.WriteAsync(samples, cancellationToken);

        public ValueTask<int?> DrainAsync(CancellationToken cancellationToken = default)
            => lane?.Playback.DrainAsync(cancellationToken) ?? ValueTask.FromResult<int?>(0);

        public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ReleaseOnceAsync().ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed)
                return;
            disposed = true;
            await ReleaseOnceAsync().ConfigureAwait(false);
        }

        private EpisodeLane GetLane()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (released)
                throw new InvalidOperationException("The physical receive playback lease is complete.");
            if (lane is not null)
                return lane;
            if (episode is not ReceivePlaybackEpisode bound)
                throw new InvalidOperationException("Receive playback was used before an episode was bound.");
            lane = owner.Acquire(bound, gain, balance, livePlaybackEnabled);
            return lane;
        }

        private async ValueTask ReleaseOnceAsync()
        {
            if (released)
                return;
            released = true;
            if (lane is not null)
                await owner.ReleaseAsync(lane).ConfigureAwait(false);
        }
    }
}
