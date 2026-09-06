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
/// Owns persisted patch-group membership and projects it into the live routing
/// coordinator. The main view model remains the XAML facade and supplies only
/// persistence, status, and patch-source reconciliation ports.
/// </summary>
internal sealed class PatchRoutingController : IDisposable, IAsyncDisposable
{
    private readonly PatchForwardingCoordinator forwarding;
    private readonly IPatchRoutingSessionPort session;
    private readonly IReadOnlyList<ChannelViewModel> channels;
    private readonly bool retainOnStartup;
    private bool disposed;

    public PatchRoutingController(
        PatchForwardingCoordinator forwarding,
        IEnumerable<ChannelViewModel> channels,
        IEnumerable<GroupConfiguration> groupDefinitions,
        bool retainOnStartup,
        IPatchRoutingSessionPort session)
    {
        this.forwarding = forwarding ?? throw new ArgumentNullException(nameof(forwarding));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.channels = channels?.Distinct().ToArray() ??
            throw new ArgumentNullException(nameof(channels));
        this.retainOnStartup = retainOnStartup;

        GroupConfiguration[] definitions = groupDefinitions?.ToArray() ??
            throw new ArgumentNullException(nameof(groupDefinitions));
        CodeplugGroupState state = session.State;
        RestoreState(definitions);
        Groups = BuildGroups(definitions, state);
        RefreshMembershipConflicts();
    }

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

        List<PatchMemberAddress> normalizedMembers = members
            .Where(member => !string.IsNullOrWhiteSpace(member.SystemName) && member.DestinationId != 0)
            .Select(member => new PatchMemberAddress(
                member.SystemName,
                member.DestinationId,
                member.ChannelName))
            .GroupBy(member => member.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        PersistGroupDefinition(groupName.Trim(), normalizedMembers, enabled, oneWay);
        ReapplyState();
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
            PersistGroupDefinition(
                group.Name,
                members,
                enabled: group.IsPatchGroup ? group.IsEnabled : true,
                oneWay: group.IsPatchGroup && group.IsOneWay);
            patchStateChanged |= group.IsPatchGroup;
        }

        if (patchStateChanged)
            ReapplyState();
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

        session.State.EnabledStates[group.Name] = group.IsEnabled;
        ReapplyState();
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
        forwarding.Dispose();
        disposed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        foreach (PatchGroupEditorViewModel group in Groups)
            group.MembershipChanged -= HandleMembershipChanged;
        disposed = true;
        await forwarding.DisposeAsync().ConfigureAwait(false);
    }

    private void PersistGroupDefinition(
        string groupName,
        IEnumerable<PatchMemberAddress> members,
        bool enabled,
        bool oneWay)
    {
        string normalizedName = groupName.Trim();
        CodeplugGroupState state = session.State;
        state.Memberships[normalizedName] = members
            .Select(PatchMemberResolver.ToSetting)
            .ToList();
        state.OneWayModes[normalizedName] = oneWay;
        state.EnabledStates[normalizedName] = enabled;
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

    private void RestoreState(IEnumerable<GroupConfiguration> groupDefinitions)
    {
        if (!retainOnStartup)
            return;
        ReapplyState(groupDefinitions);
    }

    private void ReapplyState(IEnumerable<GroupConfiguration>? groupDefinitions = null)
    {
        CodeplugGroupState state = session.State;
        IEnumerable<string> configuredPatchNames = groupDefinitions is not null
            ? groupDefinitions
                .Where(group => group.IsPatchGroup())
                .Select(group => group.Name.Trim())
            : Groups
                .Where(group => group.IsPatchGroup)
                .Select(group => group.Name);
        HashSet<string> patchGroupNames = configuredPatchNames
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var memberResolver = new PatchMemberResolver(channels);
        var memberships = new Dictionary<string, IReadOnlyList<PatchMemberAddress>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, List<PatchMemberSetting>> entry in state.Memberships)
        {
            if (!patchGroupNames.Contains(entry.Key) ||
                !state.EnabledStates.TryGetValue(entry.Key, out bool enabled) ||
                !enabled)
            {
                continue;
            }

            memberships[entry.Key] = entry.Value
                .Select(memberResolver.Resolve)
                .Where(channel => channel is not null)
                .Cast<ChannelViewModel>()
                .Select(PatchMemberResolver.FromChannel)
                .GroupBy(member => member.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
        }

        forwarding.ApplyMemberships(memberships, state.OneWayModes);
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
