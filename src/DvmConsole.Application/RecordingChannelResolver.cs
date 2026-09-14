// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;

namespace DvmConsole.Application;

public static class RecordingChannelResolver
{
    public static ChannelId? Find(IEnumerable<ChannelRuntimeDefinition> channels, RecordingCallIdentity recording)
    {
        foreach (var channel in channels)
        {
            if (channel.SystemName.Equals(recording.SystemName, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(recording.Protocol) || channel.Protocol.ToString().Equals(recording.Protocol, StringComparison.OrdinalIgnoreCase)) &&
                (recording.DestinationId is null || channel.DestinationId == recording.DestinationId) &&
                (string.IsNullOrWhiteSpace(recording.ChannelName) || channel.Name.Equals(recording.ChannelName, StringComparison.OrdinalIgnoreCase)))
                return ConsoleChannelState.GetId(channel);
        }
        return null;
    }
}
