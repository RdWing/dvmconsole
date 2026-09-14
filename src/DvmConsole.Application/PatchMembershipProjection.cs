// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;
using DvmConsole.Core.Settings;

namespace DvmConsole.Application;

/// <summary>Restores enabled patch memberships without depending on editor view models.</summary>
internal static class PatchMembershipProjection
{
    public static IReadOnlyDictionary<string, IReadOnlyList<PatchMemberAddress>> Capture(
        CodeplugGroupState state, IEnumerable<string> patchNames,
        IEnumerable<TransmitChannelDescriptor> channels)
    {
        var names = patchNames.Where(name => name.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resolver = new PatchTransmitChannelResolver(channels);
        var memberships = new Dictionary<string, IReadOnlyList<PatchMemberAddress>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in state.Memberships)
        {
            if (!names.Contains(entry.Key) || !state.EnabledStates.GetValueOrDefault(entry.Key)) continue;
            memberships[entry.Key] = entry.Value
                .Where(member => !string.IsNullOrWhiteSpace(member.SystemName) && member.DestinationId != 0)
                .Select(member => resolver.Resolve(new PatchMemberAddress(member.SystemName, member.DestinationId, member.ChannelName)))
                .OfType<TransmitChannelDescriptor>()
                .Select(PatchTransmitChannelResolver.FromChannel)
                .GroupBy(member => member.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).ToArray();
        }
        return memberships;
    }
}
