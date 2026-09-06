// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using DvmConsole.Application;
using DvmConsole.Core.Diagnostics;
using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel : IReceiveEpisodeRetirementPort
{
    bool IReceiveEpisodeRetirementPort.IsPhysicallyActive(ReceiveCallEpisodeSnapshot episode)
        => IsEpisodePhysicallyActive(Systems, episode);

    internal static bool IsEpisodePhysicallyActive(
        IReadOnlyList<SystemViewModel> systems,
        ReceiveCallEpisodeSnapshot episode)
    {
        for (int systemIndex = 0; systemIndex < systems.Count; systemIndex++)
        {
            SystemViewModel system = systems[systemIndex];
            if (!system.Name.Equals(episode.SystemName, StringComparison.OrdinalIgnoreCase))
                continue;
            for (int channelIndex = 0; channelIndex < system.Channels.Count; channelIndex++)
            {
                ChannelViewModel channel = system.Channels[channelIndex];
                for (int streamIndex = 0; streamIndex < episode.StreamIds.Count; streamIndex++)
                    if (channel.IsTrackingReceiveStream(episode.StreamIds[streamIndex]))
                        return true;
            }
            return false;
        }
        return false;
    }

    ReceiveEpisodeTargets IReceiveEpisodeRetirementPort.ResolveTargets(ReceiveCallEpisodeSnapshot episode)
    {
        ChannelViewModel[] channels = Systems
            .FirstOrDefault(system => system.Name.Equals(episode.SystemName, StringComparison.OrdinalIgnoreCase))
            ?.Channels.Where(channel => ProtocolFor(channel) == episode.Protocol &&
                channel.Definition.DestinationId == episode.DestinationId &&
                (episode.Protocol != FneTrafficProtocol.Dmr || channel.Definition.Slot == episode.Slot))
            .Distinct().ToArray() ?? [];
        return new ReceiveEpisodeTargets(
            channels.FirstOrDefault()?.Name ?? episode.DestinationId.ToString(CultureInfo.InvariantCulture),
            channels.Select(channel => new ChannelId(channel.SessionId)).ToArray());
    }

    void IReceiveEpisodeRetirementPort.ReportCompleted(DateTimeOffset now, string systemName, string message)
        => AddDebugLog(now, systemName, DebugLogSeverity.Info, message);
    void IReceiveEpisodeRetirementPort.ReportFailure(ReceiveCallEpisodeSnapshot episode, Exception exception)
        => ReportReceiveEpisodeCompletionFailure(episode, exception);
}
