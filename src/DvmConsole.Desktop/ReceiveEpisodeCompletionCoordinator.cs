namespace DvmConsole.Desktop;

// Owns the ordering boundary between per-stream receive work and logical-call
// teardown. Playback lanes and TAR recordings remain alive until every queued
// packet for every physical stream in the episode has been processed.
internal sealed class ReceiveEpisodeCompletionCoordinator
{
    private readonly ChannelReceiveWorkQueue receiveWork;
    private readonly Func<ChannelViewModel, long, Task> completePlayback;
    private readonly Action<ChannelViewModel, long> stopRecording;
    private readonly Func<ChannelViewModel, ChannelViewModel?> resolveRecordingTarget;

    public ReceiveEpisodeCompletionCoordinator(
        ChannelReceiveWorkQueue receiveWork,
        Func<ChannelViewModel, long, Task> completePlayback,
        Action<ChannelViewModel, long> stopRecording,
        Func<ChannelViewModel, ChannelViewModel?> resolveRecordingTarget)
    {
        this.receiveWork = receiveWork ?? throw new ArgumentNullException(nameof(receiveWork));
        this.completePlayback = completePlayback ?? throw new ArgumentNullException(nameof(completePlayback));
        this.stopRecording = stopRecording ?? throw new ArgumentNullException(nameof(stopRecording));
        this.resolveRecordingTarget = resolveRecordingTarget ??
            throw new ArgumentNullException(nameof(resolveRecordingTarget));
    }

    public async Task CompleteAsync(
        ReceiveCallEpisodeSnapshot episode,
        IReadOnlyList<ChannelViewModel> episodeChannels)
    {
        ArgumentNullException.ThrowIfNull(episode);
        ArgumentNullException.ThrowIfNull(episodeChannels);
        if (episodeChannels.Count == 0)
            return;

        uint[] streamIds = episode.StreamIds.Where(streamId => streamId != 0).Distinct().ToArray();
        if (streamIds.Length == 0)
            streamIds = [episode.PrimaryStreamId];

        await Task.WhenAll(episodeChannels.Select(channel =>
            receiveWork.RunAfterStreamsAsync(
                channel,
                streamIds,
                () => completePlayback(channel, episode.EpisodeId))))
            .ConfigureAwait(false);

        foreach (ChannelViewModel target in episodeChannels
                     .Select(resolveRecordingTarget)
                     .OfType<ChannelViewModel>()
                     .Distinct())
        {
            stopRecording(target, episode.EpisodeId);
        }
    }
}
