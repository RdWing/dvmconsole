// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Runtime;
using DvmConsole.Core.Settings;
using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

internal interface IPatchRoutingSessionPort
{
    CodeplugGroupState State { get; }
    void PersistSettings();
    void ReconcilePatchSources();
    void PublishStatus(string text);
}

internal sealed class PatchRoutingSessionPort(
    Func<CodeplugGroupState> getState,
    Action persistSettings,
    Action reconcilePatchSources,
    Action<string> publishStatus) : IPatchRoutingSessionPort
{
    public CodeplugGroupState State => getState();
    public void PersistSettings() => persistSettings();
    public void ReconcilePatchSources() => reconcilePatchSources();
    public void PublishStatus(string text) => publishStatus(text);
}

/// <summary>
/// Adapts desktop patch editors to the shared configuration runtime.
/// Persistence, status, and patch-source reconciliation remain host ports.
/// </summary>
internal sealed class PatchRoutingController : IDisposable
{
    private readonly PatchForwardingCoordinator forwarding;
    private readonly IPatchRoutingSessionPort session;
    private readonly IReadOnlyList<ChannelViewModel> channels;
    private readonly PatchConfigurationRuntime configuration;
    private bool disposed;

    public PatchRoutingController(
        PatchForwardingCoordinator forwarding,
        IEnumerable<ChannelViewModel> channels,
        IEnumerable<GroupConfiguration> groupDefinitions,
        bool retainOnStartup,
        IPatchRoutingSessionPort session,
        PatchConfigurationRuntime? preparedConfiguration = null)
    {
        this.forwarding = forwarding ?? throw new ArgumentNullException(nameof(forwarding));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.channels = channels?.Distinct().ToArray() ??
            throw new ArgumentNullException(nameof(channels));

        GroupConfiguration[] definitions = groupDefinitions?.ToArray() ??
            throw new ArgumentNullException(nameof(groupDefinitions));
        CodeplugGroupState state = session.State;
        configuration = preparedConfiguration ?? new PatchConfigurationRuntime(
            new PatchConfigurationPort(() => session.State,
                new ConsoleTransmitChannelDirectory(this.channels.DistinctBy(channel => channel.Id)
                    .Select(channel => (channel.SessionState, channel.ConfigurationAccess))), forwarding),
            definitions.Select(group => new PatchGroupRuntimeDefinition(group.Name, group.IsMultiselectGroup())));
        if (preparedConfiguration is null) configuration.Restore(retainOnStartup);
        Groups = BuildGroups(definitions, state);
        RefreshMembershipConflicts();
    }

    public event EventHandler? ConfigurationStateChanged
    {
        add => configuration.Changed += value;
        remove => configuration.Changed -= value;
    }
    public IReadOnlyDictionary<ChannelId, IReadOnlyList<ChannelPatchMembership>> MembershipIndex => configuration.MembershipIndex;
    public IReadOnlyList<ConsoleGroupDefinitionSnapshot> SavedGroups => configuration.SavedGroups;
    public PatchForwardingCoordinator Forwarding => forwarding;
    public IReadOnlyList<string> GroupNames => forwarding.GroupNames;
    public IReadOnlyList<PatchGroupEditorViewModel> Groups { get; }

    public void ApplyGroup(
        string groupName,
        IEnumerable<PatchMemberAddress> members,
        bool enabled,
        bool oneWay)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);
        ArgumentNullException.ThrowIfNull(members);

        configuration.SaveDefinition(groupName, members, enabled, oneWay);
        configuration.Apply();
        session.PersistSettings();
        RefreshMembershipConflicts();
        session.ReconcilePatchSources();
    }

    public string? ApplyOperatorStates(IEnumerable<PatchGroupEditorViewModel> groups)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(groups);
        PatchGroupEditorViewModel[] distinctGroups = groups.Distinct().ToArray();
        if (distinctGroups.Length == 0)
            return null;

        foreach (PatchGroupEditorViewModel group in distinctGroups)
        {
            if (group.GetMembershipValidationError() is { } validationError)
                return $"{group.GroupTypeText} group '{group.Name}' was not saved. {validationError}";
        }

        bool patchStateChanged = false;
        foreach (PatchGroupEditorViewModel group in distinctGroups)
        {
            List<PatchMemberAddress> members = group.GetMembersInRoutingOrder()
                .Select(member => PatchMemberResolver.FromChannel(member.Channel))
                .ToList();
            configuration.SaveDefinition(
                group.Name,
                members,
                enabled: group.IsPatchGroup ? group.IsEnabled : true,
                oneWay: group.IsPatchGroup && group.IsOneWay);
            patchStateChanged |= group.IsPatchGroup;
        }

        if (patchStateChanged)
            configuration.Apply();
        session.PersistSettings();
        RefreshMembershipConflicts();
        if (patchStateChanged)
            session.ReconcilePatchSources();

        session.PublishStatus(distinctGroups.Length == 1
            ? FormatAppliedGroupStatus(distinctGroups[0])
            : $"Saved operator state for {distinctGroups.Length} groups.");
        return null;
    }

    public void SetEnabled(PatchGroupEditorViewModel group)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(group);
        if (!group.IsPatchGroup)
            return;

        configuration.SetEnabled(group.Name, group.IsEnabled);
        configuration.Apply();
        session.PersistSettings();
        session.ReconcilePatchSources();
        session.PublishStatus($"Patch group '{group.Name}' {(group.IsEnabled ? "enabled" : "disabled")}.");
    }

    public void Dispose()
    {
        if (disposed)
            return;
        foreach (PatchGroupEditorViewModel group in Groups)
            group.MembershipChanged -= HandleMembershipChanged;
        disposed = true;
    }

    private IReadOnlyList<PatchGroupEditorViewModel> BuildGroups(
        IEnumerable<GroupConfiguration> groupDefinitions,
        CodeplugGroupState state)
    {
        var memberResolver = new PatchMemberResolver(channels);
        List<PatchGroupEditorViewModel> groups = [];
        foreach (GroupConfiguration definition in groupDefinitions)
        {
            string groupName = definition.Name.Trim();
            if (groupName.Length == 0)
                continue;

            List<PatchMemberSetting> savedMembers = state.Memberships
                .TryGetValue(groupName, out List<PatchMemberSetting>? configuredSettings)
                ? configuredSettings ?? []
                : [];
            List<ChannelViewModel> resolvedMembers = savedMembers
                .Select(memberResolver.Resolve)
                .Where(channel => channel is not null)
                .Cast<ChannelViewModel>()
                .Distinct()
                .ToList();
            HashSet<string> configuredMembers = resolvedMembers
                .Select(channel => PatchMemberResolver.FromChannel(channel).Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            string? configuredSourceKey = resolvedMembers.FirstOrDefault() is { } savedSource
                ? PatchMemberResolver.FromChannel(savedSource).Key
                : null;
            bool isMultiSelect = definition.IsMultiselectGroup();
            bool enabled = isMultiSelect ||
                (state.EnabledStates.TryGetValue(groupName, out bool savedEnabled) && savedEnabled);
            bool oneWay = state.OneWayModes.TryGetValue(groupName, out bool savedOneWay) && savedOneWay;
            var group = new PatchGroupEditorViewModel(
                groupName,
                enabled,
                oneWay,
                channels.Select(channel => new PatchMemberEditorViewModel(
                    channel,
                    configuredMembers.Contains(PatchMemberResolver.FromChannel(channel).Key))),
                isMultiSelect,
                configuredSourceKey);
            group.MembershipChanged += HandleMembershipChanged;
            groups.Add(group);
        }

        return groups;
    }

    private void HandleMembershipChanged(object? sender, EventArgs args)
        => RefreshMembershipConflicts();

    private void RefreshMembershipConflicts()
    {
        Dictionary<string, List<(PatchGroupEditorViewModel Group, PatchMemberEditorViewModel Member)>> memberships =
            Groups
                .SelectMany(group => group.Members
                    .Where(member => member.IsMember)
                    .Select(member => (Group: group, Member: member)))
                .GroupBy(item => item.Member.Channel.SettingsKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (PatchGroupEditorViewModel group in Groups)
        {
            List<string> conflictingChannels = [];
            foreach (PatchMemberEditorViewModel member in group.Members)
            {
                if (!member.IsMember ||
                    !memberships.TryGetValue(
                        member.Channel.SettingsKey,
                        out List<(PatchGroupEditorViewModel Group, PatchMemberEditorViewModel Member)>? owners) ||
                    owners.Count < 2)
                {
                    member.SetConflictText(null);
                    continue;
                }

                string otherGroups = string.Join(", ", owners
                    .Where(owner => !ReferenceEquals(owner.Group, group))
                    .Select(owner => owner.Group.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase));
                member.SetConflictText($"Also assigned to: {otherGroups}");
                conflictingChannels.Add(member.Channel.Name);
            }

            group.SetConflictSummary(conflictingChannels.Count == 0
                ? null
                : $"{conflictingChannels.Count} member overlap(s): " +
                  string.Join(", ", conflictingChannels.Distinct(StringComparer.OrdinalIgnoreCase)));
        }
    }

    private static string FormatAppliedGroupStatus(PatchGroupEditorViewModel group)
    {
        if (group.IsMultiSelect)
        {
            int memberCount = group.Members.Count(member => member.IsMember);
            return $"Multi-select group '{group.Name}' saved with {memberCount} member(s).";
        }

        return $"Patch group '{group.Name}' {(group.IsEnabled ? "enabled" : "disabled")}.";
    }
}
