using DvmConsole.Application;
using DvmConsole.FneClient;

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
        long revision)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(channels);
        Dictionary<ChannelId, ChannelControlSnapshot> channelSnapshots = channels.ToDictionary(
            pair => pair.Key,
            pair => ProjectChannel(owner, pair.Key, pair.Value));
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
        ChannelViewModel channel)
    {
        TargetAuthorityState authority = channel.TalkgroupAvailability switch
        {
            FneTalkgroupAvailability.Available => TargetAuthorityState.Available,
            FneTalkgroupAvailability.Unavailable => TargetAuthorityState.Unavailable,
            _ => TargetAuthorityState.Pending
        };
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
            Recording: owner.IsChannelRecording(channel),
            RecordingFinalizing: owner.IsChannelRecordingFinalizing(channel),
            RecordingFault: null,
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
            Patches: ProjectPatches(owner, channel),
            PendingOperation: null,
            Fault: null,
            RecordingPlayback: owner.IsChannelRecordingPlaybackActive(channel),
            TransmitEncryptionConfigured: channel.Definition.IsEncrypted,
            TransmitEncryptionSelectable: channel.Definition.SelectableEncryption);
    }

    private static IReadOnlyList<ChannelPatchMembership> ProjectPatches(
        MainWindowViewModel owner,
        ChannelViewModel channel)
        => owner.PatchGroups
            .Where(group => group.Members.Any(member =>
                member.IsMember && ReferenceEquals(member.Channel, channel)))
            .Select(group => new ChannelPatchMembership(
                PatchId.FromName(group.Name),
                group.Name,
                group.IsEnabled,
                group.IsOneWay,
                group.IsOneWay && ReferenceEquals(group.SelectedSource?.Channel, channel)))
            .ToArray();

    private static string ResolveProtocol(IEnumerable<ChannelViewModel> channels)
        => channels.Select(channel => channel.Definition.Protocol.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? "Unknown";
}
