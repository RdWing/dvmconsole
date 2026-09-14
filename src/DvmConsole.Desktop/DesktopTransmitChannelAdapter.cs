// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;

namespace DvmConsole.Desktop;

internal static class DesktopTransmitChannelAdapter
{
    public static TransmitChannelDescriptor ToTransmitDescriptor(this ChannelViewModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return channel.SessionState.CaptureTransmitDescriptor(channel.ConfigurationAccess);
    }

}
