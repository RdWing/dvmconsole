// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;
using DvmConsole.Media;

namespace DvmConsole.Application;

internal static class ReceivePlaybackEpisodeResolver
{
    public static ReceivePlaybackEpisode Resolve(ConsoleChannelState channel, ReceiveCallEpisodeTracker episodes, IRadioMediaFrame traffic)
        => episodes.TryGet(channel.Runtime.Definition.SystemName, ChannelProtocolMediaMapper.ToTrafficProtocol(channel.Runtime.Definition.Protocol), traffic.StreamId, out var episode)
            ? new(episode.EpisodeId, episode.PrimaryStreamId, traffic.StreamId, RetainUntilEpisodeCompletion: true)
            : new(-checked((long)traffic.StreamId), traffic.StreamId, traffic.StreamId, RetainUntilEpisodeCompletion: false);
}
