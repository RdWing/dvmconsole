// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.FneClient;
using DvmConsole.Presentation;
using System.Collections.Immutable;

namespace DvmConsole.Desktop;

internal static class DesktopConsoleSnapshotProjector
{
    public static ConsoleTopologySnapshot BuildTopology(MainWindowViewModel owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        SystemDescriptor[] systems = owner.Systems
            .Select(system => new SystemDescriptor(
                SystemId.FromName(system.Name),
                system.Name,
                ResolveProtocol(system.Channels)))
            .ToArray();
        ZoneDescriptor[] zones = owner.Zones
            .Select(zone => new ZoneDescriptor(
                ZoneId.FromName(zone.Name),
                zone.Name,
                zone.Channels.Select(channel => new ChannelId(channel.SessionId)).ToArray()))
            .ToArray();
        ChannelDescriptor[] channelDescriptors = owner.Zones
            .SelectMany(zone => zone.Channels.Select(channel => (Zone: zone, Channel: channel)))
            .GroupBy(pair => new ChannelId(pair.Channel.SessionId))
            .Select(group => ProjectDescriptor(group.Key, group.First().Zone, group.First().Channel))
            .ToArray();
        return new ConsoleTopologySnapshot(owner.ConfigurationReference, systems, zones, channelDescriptors);
    }

    public static ConsoleRuntimeSnapshot BuildSnapshot(
        MainWindowViewModel owner,
        IReadOnlyDictionary<ChannelId, ChannelViewModel> channels,
        long revision,
        ConsoleRuntimeSnapshot? previous = null,
        IReadOnlyCollection<ChannelId>? dirtyChannels = null,
        IReadOnlyDictionary<ChannelId, IReadOnlyList<ChannelPatchMembership>>? patchIndex = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(channels);
        ChannelId[] projectedIds = previous is null || dirtyChannels is null
            ? channels.Keys.ToArray()
            : dirtyChannels.Where(channels.ContainsKey).Distinct().ToArray();
        IReadOnlyDictionary<ChannelId, ChannelRecordingState> recording =
            owner.CaptureChannelRecordingState(projectedIds.Select(id => channels[id]));
        IReadOnlyDictionary<ChannelId, IReadOnlyList<ChannelPatchMembership>> patches =
            patchIndex ?? BuildPatchIndex(owner);
        ImmutableDictionary<ChannelId, ChannelControlSnapshot> channelSnapshots = UpdateProjection(
            previous?.Channels,
            projectedIds,
            id => ProjectChannel(owner, id, channels[id], recording, patches));
        return new ConsoleRuntimeSnapshot(
            revision,
            owner.ConfigurationReference,
            channelSnapshots,
            false,
            owner.StatusText);
    }

    private static ChannelDescriptor ProjectDescriptor(
        ChannelId id,
        ZoneViewModel zone,
        ChannelViewModel channel)
        => new(
            id,
            SystemId.FromName(channel.Definition.SystemName),
            ZoneId.FromName(zone.Name),
            channel.Name,
            channel.Definition.DestinationId,
            channel.Definition.Protocol.ToString(),
            channel.Definition.Slot,
            channel.Definition.RxOnly,
            channel.HasCallPriority);

    private static ChannelControlSnapshot ProjectChannel(
        MainWindowViewModel owner,
        ChannelId id,
        ChannelViewModel channel,
        IReadOnlyDictionary<ChannelId, ChannelRecordingState> recording,
        IReadOnlyDictionary<ChannelId, IReadOnlyList<ChannelPatchMembership>> patches)
    {
        TargetAuthorityState authority = channel.TalkgroupAvailability switch
        {
            FneTalkgroupAvailability.Available => TargetAuthorityState.Available,
            FneTalkgroupAvailability.Unavailable => TargetAuthorityState.Unavailable,
            _ => TargetAuthorityState.Pending
        };
        recording.TryGetValue(id, out ChannelRecordingState recordingState);
        return new ChannelControlSnapshot(
            id,
            channel.State,
            channel.StateText,
            channel.LastCallerText,
            channel.IsAudioEnabled,
            channel.IsReceivePresentationActive,
            channel.IsTransmitting,
            channel.IsTransmitSelected,
            channel.IsPageSelected,
            channel.IsAlertSelected,
            Recording: recordingState.IsRecording,
            RecordingFinalizing: recordingState.IsFinalizing,
            RecordingFault: recordingState.Fault,
            TarArmed: channel.IsRecordingEnabled,
            OutputRoute: channel.OutputDeviceIdText,
            Gain: channel.Volume,
            Balance: channel.StereoBalance,
            EffectiveMuteReason: owner.GetEffectiveOutputMuteReason(channel),
            Authority: authority,
            AuthorityReason: authority == TargetAuthorityState.Unavailable
                ? channel.TalkgroupUnavailableReason
                : null,
            ObservedReceiveEncrypted: channel.ObservedReceiveEncrypted,
            SelectedTransmitEncrypted: channel.IsTransmitEncrypted,
            TransmitKeyAvailable: channel.TransmitKeyAvailable,
            Patches: patches.GetValueOrDefault(id, []),
            PendingOperation: null,
            Fault: null,
            RecordingPlayback: owner.IsChannelRecordingPlaybackActive(channel),
            TransmitEncryptionConfigured: channel.Definition.IsEncrypted,
            TransmitEncryptionSelectable: channel.Definition.SelectableEncryption);
    }

    internal static IReadOnlyDictionary<ChannelId, IReadOnlyList<ChannelPatchMembership>> BuildPatchIndex(
        MainWindowViewModel owner)
    {
        var result = new Dictionary<ChannelId, List<ChannelPatchMembership>>();
        foreach (PatchGroupEditorViewModel group in owner.PatchGroups)
        {
            foreach (PatchMemberEditorViewModel member in group.Members.Where(member => member.IsMember))
            {
                ChannelId id = member.Channel.Id;
                if (!result.TryGetValue(id, out List<ChannelPatchMembership>? memberships))
                {
                    memberships = [];
                    result.Add(id, memberships);
                }
                memberships.Add(new ChannelPatchMembership(
                    PatchId.FromName(group.Name),
                    group.Name,
                    group.IsEnabled,
                    group.IsOneWay,
                    group.IsOneWay && ReferenceEquals(group.SelectedSource?.Channel, member.Channel)));
            }
        }
        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<ChannelPatchMembership>)pair.Value);
    }

    internal static ImmutableDictionary<TKey, TValue> UpdateProjection<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue>? previous,
        IEnumerable<TKey> dirtyKeys,
        Func<TKey, TValue> project)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(dirtyKeys);
        ArgumentNullException.ThrowIfNull(project);
        ImmutableDictionary<TKey, TValue> current = previous switch
        {
            null => ImmutableDictionary<TKey, TValue>.Empty,
            ImmutableDictionary<TKey, TValue> immutable => immutable,
            _ => previous.ToImmutableDictionary()
        };
        foreach (TKey key in dirtyKeys)
            current = current.SetItem(key, project(key));
        return current;
    }

    private static string ResolveProtocol(IEnumerable<ChannelViewModel> channels)
        => channels.Select(channel => channel.Definition.Protocol.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? "Unknown";
}
