// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>
/// Identifies the logical receive episode whose queued physical streams must
/// finish before playback and recording are torn down.
/// </summary>
public sealed record ReceiveEpisodeCompletion(
    long EpisodeId,
    uint PrimaryStreamId,
    IReadOnlyList<uint> StreamIds);

/// <summary>
/// Host integration needed to preserve per-channel receive ordering while the
/// application layer coordinates logical episode teardown.
/// </summary>
public interface IReceiveEpisodeCompletionPort
{
    Task RunAfterStreamsAsync(
        ChannelId channelId,
        IReadOnlyCollection<uint> streamIds,
        Func<Task> continuation);

    Task CompletePlaybackAsync(ChannelId channelId, long episodeId);

    ChannelId? ResolveRecordingTarget(ChannelId channelId);

    void StopRecording(ChannelId channelId, long episodeId);
}

internal interface ICancellableReceiveEpisodeCompletionPort
{
    Task RunAfterStreamsAsync(
        ChannelId channelId,
        IReadOnlyCollection<uint> streamIds,
        Func<CancellationToken, Task> continuation);

    Task CompletePlaybackAsync(
        ChannelId channelId,
        long episodeId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Owns the ordering boundary between per-stream receive work and logical-call
/// teardown without depending on presentation objects or platform services.
/// </summary>
public sealed class ReceiveEpisodeCompletionCoordinator
{
    private readonly IReceiveEpisodeCompletionPort port;

    public ReceiveEpisodeCompletionCoordinator(IReceiveEpisodeCompletionPort port)
        => this.port = port ?? throw new ArgumentNullException(nameof(port));

    public async Task CompleteAsync(
        ReceiveEpisodeCompletion episode,
        IReadOnlyList<ChannelId> episodeChannels)
    {
        ArgumentNullException.ThrowIfNull(episode);
        ArgumentNullException.ThrowIfNull(episodeChannels);
        if (episodeChannels.Count == 0)
            return;

        uint[] streamIds = episode.StreamIds
            .Where(streamId => streamId != 0)
            .Distinct()
            .ToArray();
        if (streamIds.Length == 0)
        {
            if (episode.PrimaryStreamId == 0)
                throw new ArgumentException("A receive episode must identify at least one stream.", nameof(episode));
            streamIds = [episode.PrimaryStreamId];
        }

        ChannelId[] channels = episodeChannels.Distinct().ToArray();
        await Task.WhenAll(channels.Select(channelId =>
            CompletePlaybackAfterStreamsAsync(channelId, streamIds, episode.EpisodeId)))
            .ConfigureAwait(false);

        foreach (ChannelId target in channels
                     .Select(port.ResolveRecordingTarget)
                     .OfType<ChannelId>()
                     .Distinct())
        {
            port.StopRecording(target, episode.EpisodeId);
        }
    }

    private Task CompletePlaybackAfterStreamsAsync(
        ChannelId channelId,
        IReadOnlyCollection<uint> streamIds,
        long episodeId)
    {
        if (port is ICancellableReceiveEpisodeCompletionPort cancellable)
        {
            return cancellable.RunAfterStreamsAsync(
                channelId,
                streamIds,
                cancellationToken => cancellable.CompletePlaybackAsync(
                    channelId,
                    episodeId,
                    cancellationToken));
        }

        return port.RunAfterStreamsAsync(
            channelId,
            streamIds,
            () => port.CompletePlaybackAsync(channelId, episodeId));
    }
}
