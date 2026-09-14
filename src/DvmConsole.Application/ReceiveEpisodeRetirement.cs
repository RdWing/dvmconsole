// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Threading;
using DvmConsole.Core.Runtime;

namespace DvmConsole.Application;

internal sealed record ReceiveEpisodeTargets(string Name, IReadOnlyList<ChannelId> Channels);

internal interface IReceiveEpisodeRetirementPort
{
    void ProjectCompletion(ConsoleCallCompletion completion);
    void ReportCompleted(DateTimeOffset now, string systemName, string message);
    void ReportFailure(ReceiveCallEpisodeSnapshot episode, Exception exception);
}

/// <summary>Completes logical history once, then retires playback and recording behind queued receive work.</summary>
internal sealed class ReceiveEpisodeRetirement(
    ReceiveCallEpisodeTracker episodes,
    ReceiveEpisodeCompletionCoordinator completion,
    ReceiveEpisodeTargetIndex targetIndex,
    ConsoleCallHistory history, IReceiveEpisodeRetirementPort port)
{
    public bool Advance(DateTimeOffset now)
    {
        bool historyChanged = false;
        foreach (ReceiveCallEpisodeSnapshot episode in episodes.Advance(now, episode => !targetIndex.IsPhysicallyActive(episode)))
        {
            var completed = history.CompleteReceive(episode.SystemName, episode.Protocol,
                episode.PrimaryStreamId, episode.PresentationEndAt, receiveEpisodeId: episode.EpisodeId);
            if (completed is not null)
            {
                historyChanged = true;
                port.ProjectCompletion(completed);
            }
            ReceiveEpisodeTargets targets = targetIndex.Resolve(episode);
            double seconds = Math.Max(0, (episode.PresentationEndAt - episode.StartedAt).TotalSeconds);
            port.ReportCompleted(now, episode.SystemName,
                $"RX logical call episode ended on {targets.Name}: " +
                $"{episode.Protocol.ToString().ToUpperInvariant()} {episode.SourceId}→{episode.DestinationId}, " +
                $"episode {episode.EpisodeId}, {episode.StreamIds.Count} physical stream" +
                $"{(episode.StreamIds.Count == 1 ? string.Empty : "s")}, duration {seconds:0.0} s.");
            if (targets.Channels.Count > 0)
                TaskObservation.Observe(completion.CompleteAsync(
                    new ReceiveEpisodeCompletion(episode.EpisodeId, episode.PrimaryStreamId, episode.StreamIds),
                    targets.Channels), exception => port.ReportFailure(episode, exception));
        }
        return historyChanged;
    }
}
