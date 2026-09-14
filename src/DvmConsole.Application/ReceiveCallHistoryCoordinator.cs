// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Diagnostics;
using DvmConsole.Core.Runtime;
using DvmConsole.Operations;

namespace DvmConsole.Application;

internal interface IReceiveCallHistoryPresentation
{
    string DescribeSignalQuality(IRadioMediaFrame traffic);
    void Project(ConsoleCallHistoryRecord record);
    void Log(DateTimeOffset timestamp, string source, DebugLogSeverity severity, string message);
}

/// <summary>Creates and updates receive history from shared call-episode decisions.</summary>
internal sealed class ReceiveCallHistoryCoordinator(
    ConsoleCallHistory history, ConsoleChannelMediaDirectory channels, IReceiveCallHistoryPresentation presentation)
{
    public bool Observe(ReceiveIngressSystem system, ConsoleChannelState channel,
        ReceiveIngressDecision decision, ReceiveStreamDecision applied, EncryptionSnapshot encryption)
    {
        IRadioMediaFrame traffic = decision.Traffic;
        ReceiveCallEpisodeSnapshot? episode = decision.EpisodeSnapshot;
        uint primaryStream = decision.EpisodeObservation?.PrimaryStreamId ?? traffic.StreamId;
        CallId? call = history.FindActiveReceive(system.Name, traffic.Protocol, primaryStream,
            channel.Identity.Name, traffic.DestinationId, episode?.EpisodeId);
        bool canStart = applied.Transition is ReceiveStreamTransition.Started or
            ReceiveStreamTransition.Restarted or ReceiveStreamTransition.Colliding or
            ReceiveStreamTransition.Continued or ReceiveStreamTransition.Resumed;
        bool changed = false;
        if (canStart && traffic.SourceId != 0 && call is null)
        {
            presentation.Log(decision.ReceivedAt, system.Name, DebugLogSeverity.Info,
                $"RX logical call episode started on {channel.Identity.Name}: " +
                $"{traffic.Protocol.ToString().ToUpperInvariant()} {traffic.CallType}, " +
                $"{traffic.SourceId}→{traffic.DestinationId}, episode {episode?.EpisodeId}, " +
                $"primary physical stream {primaryStream}" +
                (!encryption.IsKnown ? ", encryption unknown" : encryption.IsSecure ? ", encrypted" : ", clear") +
                $"{presentation.DescribeSignalQuality(traffic)}.");
            call = CallId.New();
            history.Add(new ConsoleCallHistoryRecord(call.Value, episode?.StartedAt ?? decision.ReceivedAt,
                null, system.Id, system.Name, channel.Id, channel.Identity.Name, traffic.Protocol,
                traffic.SourceId, traffic.DestinationId, primaryStream, [primaryStream], episode?.EpisodeId,
                channels.LastCallerText(channel.Id), ConsoleCallDirection.Receive,
                new(encryption.IsKnown, encryption.IsSecure, encryption.AlgorithmId, encryption.KeyId),
                string.Empty, string.Empty, string.Empty, string.Empty));
            changed = true;
        }
        if (call is not { } id) return false;
        if (episode is not null)
            foreach (uint stream in episode.StreamIds)
                changed = history.ObserveStream(id, stream) || changed;
        if (encryption.IsKnown)
            changed = history.UpdateEncryption(id,
                new(true, encryption.IsSecure, encryption.AlgorithmId, encryption.KeyId)) || changed;
        if (changed && history.Find(id) is { } record)
            presentation.Project(record);
        return changed;
    }
}
