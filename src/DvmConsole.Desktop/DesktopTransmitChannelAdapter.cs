// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;

namespace DvmConsole.Desktop;

internal static class DesktopTransmitChannelAdapter
{
    public static TransmitChannelDescriptor ToTransmitDescriptor(this ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return new TransmitChannelDescriptor(
            new ChannelId(channel.SessionId),
            channel.Definition,
            channel.IsReceivePresentationActive,
            channel.IsTransmitEncrypted,
            channel.CanTransmitByConfiguration,
            channel.ConfigurationTransmitUnavailableReason,
            channel.TalkgroupUnavailableReason,
            channel.HasCallPriority);
    }

}
