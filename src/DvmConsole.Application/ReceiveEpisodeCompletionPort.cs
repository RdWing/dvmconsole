// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>
/// Orders episode completion through the shared receive workers and playback
/// coordinator. The storage owner supplies recording finalization by ID.
/// </summary>
internal sealed class ReceiveEpisodeCompletionPort :
    IReceiveEpisodeCompletionPort,
    ICancellableReceiveEpisodeCompletionPort
{
    private readonly ChannelReceiveWorkQueue receiveWork;
    private readonly ChannelReceiveAudioCoordinator receiveAudio;
    private readonly Action<ChannelId, long> stopRecording;
    private readonly IReadOnlyDictionary<ChannelId, ConsoleChannelState> channels;
    private readonly Func<ChannelId, ChannelId?> resolveRecordingTarget;

    public ReceiveEpisodeCompletionPort(
        ChannelReceiveWorkQueue receiveWork,
        ChannelReceiveAudioCoordinator receiveAudio,
        Action<ChannelId, long> stopRecording,
        IReadOnlyDictionary<ChannelId, ConsoleChannelState> channels,
        Func<ChannelId, ChannelId?> resolveRecordingTarget)
    {
        this.receiveWork = receiveWork ?? throw new ArgumentNullException(nameof(receiveWork));
        this.receiveAudio = receiveAudio ?? throw new ArgumentNullException(nameof(receiveAudio));
        this.stopRecording = stopRecording ?? throw new ArgumentNullException(nameof(stopRecording));
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

    Task ICancellableReceiveEpisodeCompletionPort.RunAfterStreamsAsync(
        ChannelId channelId,
        IReadOnlyCollection<uint> streamIds,
        Func<CancellationToken, Task> continuation)
        => receiveWork.RunAfterStreamsAsync(channelId, streamIds, continuation);

    Task ICancellableReceiveEpisodeCompletionPort.CompletePlaybackAsync(
        ChannelId channelId,
        long episodeId,
        CancellationToken cancellationToken)
        => receiveAudio.CompleteEpisodeAsync(
            Resolve(channelId),
            episodeId,
            cancellationToken);

    public ChannelId? ResolveRecordingTarget(ChannelId channelId)
        => resolveRecordingTarget(Resolve(channelId));

    public void StopRecording(ChannelId channelId, long episodeId)
        => stopRecording(Resolve(channelId), episodeId);

    private ChannelId Resolve(ChannelId channelId)
        => channels.ContainsKey(channelId) ? channelId
            : throw new KeyNotFoundException($"Channel '{channelId}' is not part of the active session.");
}
