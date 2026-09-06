// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Desktop;

// Presentation of the same resolved channel set used by the send operation.
// Rebuilt only when target selections change, never on audio-frame updates.
internal sealed record ToneTargetSummary(string Text, string Details)
{
    public static ToneTargetSummary Create(string selection, IEnumerable<ChannelViewModel> channels)
    {
        string[] targets = channels.Select(channel => $"{channel.SystemName} / {channel.Name}").ToArray();
        return new(
            $"{selection}: {targets.Length} armed",
            targets.Length == 0
                ? $"Enable {selection} on channel cards to choose destinations."
                : string.Join(Environment.NewLine, targets));
    }
}
