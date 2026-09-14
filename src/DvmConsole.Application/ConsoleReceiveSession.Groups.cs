// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;
using DvmConsole.Core.Settings;
using System.Collections.Immutable;

namespace DvmConsole.Application;

public sealed partial class ConsoleReceiveSession : IConsoleGroupSettings, IConsoleGroupSelectionCommands, IPatchConfigurationPort
{
    private CodeplugGroupState groupSettings = new();
    private PatchConfigurationRuntime groupConfiguration => operationalRuntime.PatchConfiguration
        ?? throw new InvalidOperationException("Patch configuration is not initialized.");
    private bool restorePatches;
    private ImmutableDictionary<ChannelId, IReadOnlyList<ChannelPatchMembership>> groupMemberships
        = ImmutableDictionary<ChannelId, IReadOnlyList<ChannelPatchMembership>>.Empty;
    private ImmutableHashSet<string> enabledPatchGroups = ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> EnabledPatchGroups => Volatile.Read(ref enabledPatchGroups);
    public IReadOnlyList<ConsoleGroupDefinitionSnapshot> SavedGroups => groupConfiguration.SavedGroups;
    public bool RestorePatchesOnStartup => Volatile.Read(ref restorePatches);

    public async Task<int> AddGroupToTransmitSelectionAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        int selectedCount = 0;
        await RunCommandAsync(async token =>
        {
            var group = SavedGroups.FirstOrDefault(candidate => candidate.IsMultiSelect &&
                candidate.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException("Choose a saved multi-select group.", nameof(name));
            if (group.UnresolvedMembers > 0)
                throw new InvalidOperationException("Review and save the unavailable group members before selecting this group.");
            var result = await transmitControls.SetSelectionAsync(group.Members, selected: true,
                applyState: update =>
                {
                    lock (ingressSync)
                    {
                        ObjectDisposedException.ThrowIf(IsStopping, this);
                        update();
                    }
                }, token: token).ConfigureAwait(false);
            if (result.Count == 0)
                throw new InvalidOperationException("This group has no transmit-capable members.");
            selectedCount = result.Count;
            SetStatus($"Group '{group.Name}': {selectedCount} channel(s) selected for TX.");
        }, cancellationToken).ConfigureAwait(false);
        return selectedCount;
    }

    private async Task RestoreGroupPreferencesAsync(CancellationToken token)
    {
        if (dependencies.Preferences is not IConsoleGroupPreferences store) return;
        var saved = await store.LoadGroupsAsync(token).ConfigureAwait(false);
        groupSettings = saved.Groups.Clone();
        restorePatches = saved.RestorePatchesOnStartup;
        groupConfiguration.RefreshSnapshot();
        if (restorePatches && dependencies.ManualInput is not null)
            enabledPatchGroups = SavedGroups.Where(group => !group.IsMultiSelect && group.SavedEnabled)
                .Select(group => group.Name).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        PublishGroupMemberships();
    }

    public Task SaveGroupAsync(string name, IReadOnlyList<ChannelId> members, bool enabled, bool oneWay,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(members);
        ChannelId[] captured = members.Distinct().ToArray();
        return RunCommandAsync(async token =>
        {
            var definition = SavedGroups.FirstOrDefault(group => string.Equals(group.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException("The group is not part of this configuration.", nameof(name));
            var selected = captured.Select(id => state.Channels.TryGetValue(id, out var channel) ? channel
                : throw new ArgumentException("A group member is not part of this configuration.", nameof(members))).ToArray();
            if (!definition.IsMultiSelect && enabled && selected.Length < 2)
                throw new ArgumentException("An enabled patch needs at least two channels.", nameof(members));
            if (!definition.IsMultiSelect && enabled && dependencies.ManualInput is null)
                throw new NotSupportedException("This host does not support patch transmission.");
            string? validationError = PatchMembershipValidation.Validate(
                captured.Select(transmitChannels.CapturePatchCapabilities).ToArray(),
                !definition.IsMultiSelect && oneWay);
            if (validationError is not null)
                throw new ArgumentException(validationError, nameof(members));
            var settings = selected.Select(channel => new PatchMemberSetting
            {
                SystemName = channel.Runtime.Definition.SystemName,
                ChannelName = channel.Runtime.Definition.Name,
                DestinationId = channel.Runtime.Definition.DestinationId
            }).ToArray();
            var store = dependencies.Preferences as IConsoleGroupPreferences
                ?? throw new NotSupportedException("Group preferences are unavailable.");
            bool savedEnabled = definition.IsMultiSelect || enabled;
            bool savedOneWay = !definition.IsMultiSelect && oneWay;
            await store.SaveGroupAsync(definition.Name, settings, savedEnabled, savedOneWay, token).ConfigureAwait(false);
            groupConfiguration.SaveDefinition(definition.Name, settings.Select(member => new PatchMemberAddress(
                member.SystemName, member.DestinationId, member.ChannelName)), savedEnabled, savedOneWay);
            if (!definition.IsMultiSelect)
            {
                Volatile.Write(ref enabledPatchGroups, enabled ? enabledPatchGroups.Add(definition.Name)
                    : enabledPatchGroups.Remove(definition.Name));
                PublishGroupMemberships();
                await RefreshLivePatchesAsync(token).ConfigureAwait(false);
            }
            else PublishGroupMemberships();
            SetStatus($"Group '{definition.Name}' saved.");
        }, cancellationToken).AsTask();
    }

    private IEnumerable<KeyValuePair<ChannelId, uint>> CaptureCurrentPatchStreams()
        => state.Channels.Select(pair => new KeyValuePair<ChannelId, uint>(pair.Key, pair.Value.Runtime.StreamId ?? 0));

    private void PublishGroupMemberships()
    {
        var multiSelectNames = SavedGroups.Where(group => group.IsMultiSelect).Select(group => group.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Volatile.Write(ref groupMemberships, groupConfiguration.MembershipIndex.ToImmutableDictionary(pair => pair.Key,
            pair => (IReadOnlyList<ChannelPatchMembership>)pair.Value.Select(membership => membership with
            { IsEnabled = multiSelectNames.Contains(membership.Name) || enabledPatchGroups.Contains(membership.Name) }).ToImmutableArray()));
        Changed();
    }

    // Command ownership serializes definitions/decoder replacement. Ingress admission
    // closes first and is reopened only after both graph updates have completed.
    private async Task RefreshLivePatchesAsync(CancellationToken token, bool rebuildDecoders = false)
    {
        if (dependencies.ManualInput is null || IsStopping) return;
        Task paused;
        lock (ingressSync) paused = patchPause = patches.PauseAsync();
        await paused.WaitAsync(token).ConfigureAwait(false);
        if (rebuildDecoders) await patches.Decoder.StopAllAsync(token).ConfigureAwait(false);
        var groups = SavedGroups.Where(group => !group.IsMultiSelect && enabledPatchGroups.Contains(group.Name)
            && group.UnresolvedMembers == 0 && group.Members.Length >= 2).ToArray();
        var sourceIds = PatchSourceSelectionPolicy.SelectEnabledSources(groups);
        await patches.Decoder.ApplyChannelsAsync(sourceIds.Select(state.Media.DescribeReceive), token)
            .ConfigureAwait(false);
        var memberships = groups.ToDictionary(group => group.Name,
            group => (IReadOnlyList<PatchMemberAddress>)group.Members.Select(id =>
            {
                var definition = state.Channels[id].Runtime.Definition;
                return new PatchMemberAddress(definition.SystemName, definition.DestinationId, definition.Name);
            }).ToArray(), StringComparer.OrdinalIgnoreCase);
        lock (ingressSync)
        {
            token.ThrowIfCancellationRequested();
            if (IsStopping) return;
            patches.Forwarding.ApplyMemberships(memberships, groups.ToDictionary(group => group.Name, group => group.OneWay,
                StringComparer.OrdinalIgnoreCase));
            if (!audioUnavailable) patches.Resume(CaptureCurrentPatchStreams());
        }
    }

    public Task SetRestorePatchesAsync(bool restore, CancellationToken cancellationToken = default)
        => RunCommandAsync(async token =>
        {
            var store = dependencies.Preferences as IConsoleGroupPreferences
                ?? throw new NotSupportedException("Group preferences are unavailable.");
            await store.SaveRestorePatchesAsync(restore, token).ConfigureAwait(false);
            Volatile.Write(ref restorePatches, restore);
            SetStatus(restore ? "Saved patches will restore on startup." : "Patches will start disabled; saved groups are retained.");
        }, cancellationToken).AsTask();

    CodeplugGroupState IPatchConfigurationPort.State => groupSettings;
    IReadOnlyList<TransmitChannelDescriptor> IPatchConfigurationPort.CaptureChannels()
        => transmitChannels.CaptureAll();
    void IPatchConfigurationPort.ApplyMemberships(IReadOnlyDictionary<string, IReadOnlyList<PatchMemberAddress>> memberships,
        IReadOnlyDictionary<string, bool> oneWay) => patches.Forwarding.ApplyMemberships(memberships, oneWay);
}
