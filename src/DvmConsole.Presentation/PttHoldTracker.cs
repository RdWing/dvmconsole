// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;

namespace DvmConsole.Presentation;

// Retains the channel behind a press-and-hold input independently of the
// renderer's visual tree. Capture loss can then release the original channel
// even after the source button has moved or been detached.
internal sealed class PttHoldTracker<TInput> where TInput : notnull
{
    private readonly Dictionary<TInput, ChannelId> channels = [];

    public void Track(TInput input, ChannelId channelId)
        => channels[input] = channelId;

    public ChannelId? Take(TInput input)
    {
        if (!channels.Remove(input, out ChannelId channelId))
            return null;
        return channelId;
    }

    public void Clear() => channels.Clear();
}
