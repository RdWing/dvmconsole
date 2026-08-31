using DvmConsole.Application;

namespace DvmConsole.Desktop;

/// <summary>
/// Adapts the current desktop receive queue and recording implementation to
/// the stable-ID application boundary. Presentation objects do not cross into
/// the application coordinator.
/// </summary>
internal sealed class DesktopReceiveEpisodeCompletionPort : IReceiveEpisodeCompletionPort
{
    private readonly ChannelReceiveWorkQueue receiveWork;
    private readonly ChannelReceiveAudioCoordinator receiveAudio;
    private readonly CallRecordingManager recordings;
    private readonly IReadOnlyDictionary<ChannelId, ChannelViewModel> channels;
    private readonly Func<ChannelViewModel, ChannelViewModel?> resolveRecordingTarget;

    public DesktopReceiveEpisodeCompletionPort(
        ChannelReceiveWorkQueue receiveWork,
        ChannelReceiveAudioCoordinator receiveAudio,
        CallRecordingManager recordings,
        IReadOnlyDictionary<ChannelId, ChannelViewModel> channels,
        Func<ChannelViewModel, ChannelViewModel?> resolveRecordingTarget)
    {
        this.receiveWork = receiveWork ?? throw new ArgumentNullException(nameof(receiveWork));
        this.receiveAudio = receiveAudio ?? throw new ArgumentNullException(nameof(receiveAudio));
        this.recordings = recordings ?? throw new ArgumentNullException(nameof(recordings));
        this.channels = channels ?? throw new ArgumentNullException(nameof(channels));
        this.resolveRecordingTarget = resolveRecordingTarget ??
            throw new ArgumentNullException(nameof(resolveRecordingTarget));
    }

    public Task RunAfterStreamsAsync(
        ChannelId channelId,
        IReadOnlyCollection<uint> streamIds,
        Func<Task> continuation)
        => receiveWork.RunAfterStreamsAsync(channelId, streamIds, continuation);

    public Task CompletePlaybackAsync(ChannelId channelId, long episodeId)
        => receiveAudio.CompleteEpisodeAsync(Resolve(channelId), episodeId);

    public ChannelId? ResolveRecordingTarget(ChannelId channelId)
    {
        ChannelViewModel? target = resolveRecordingTarget(Resolve(channelId));
        return target is null ? null : new ChannelId(target.SessionId);
    }

    public void StopRecording(ChannelId channelId, long episodeId)
        => recordings.StopEpisode(Resolve(channelId), episodeId);

    private ChannelViewModel Resolve(ChannelId channelId)
        => channels.TryGetValue(channelId, out ChannelViewModel? channel)
            ? channel
            : throw new KeyNotFoundException($"Channel '{channelId}' is not part of the active session.");
}
