// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using DvmConsole.Core.Runtime;
using DvmConsole.Core.Settings;

namespace DvmConsole.Application;

internal interface IPatchConfigurationPort
{
    CodeplugGroupState State { get; }
    IReadOnlyList<TransmitChannelDescriptor> CaptureChannels();
    void ApplyMemberships(IReadOnlyDictionary<string, IReadOnlyList<PatchMemberAddress>> memberships,
        IReadOnlyDictionary<string, bool> oneWay);
}

internal sealed class PatchConfigurationPort(
    Func<CodeplugGroupState> getState,
    ConsoleTransmitChannelDirectory channels,
    PatchForwardingCoordinator forwarding) : IPatchConfigurationPort
{
    public CodeplugGroupState State => getState();
    public IReadOnlyList<TransmitChannelDescriptor> CaptureChannels() => channels.CaptureAll();
    public void ApplyMemberships(IReadOnlyDictionary<string, IReadOnlyList<PatchMemberAddress>> memberships,
        IReadOnlyDictionary<string, bool> oneWay) => forwarding.ApplyMemberships(memberships, oneWay);
}

internal sealed record PatchGroupRuntimeDefinition(string Name, bool IsMultiSelect = false);

/// <summary>Owns saved membership updates and restoration into live patch routing.</summary>
internal sealed class PatchConfigurationRuntime
{
    private readonly IPatchConfigurationPort port;
    private readonly string[] patchNames;
    private readonly PatchGroupRuntimeDefinition[] groups;
    private ImmutableDictionary<ChannelId, IReadOnlyList<ChannelPatchMembership>> membershipIndex
        = ImmutableDictionary<ChannelId, IReadOnlyList<ChannelPatchMembership>>.Empty;

    public event EventHandler? Changed;
    private ImmutableList<ConsoleGroupDefinitionSnapshot> savedGroups = [];
    public IReadOnlyList<ConsoleGroupDefinitionSnapshot> SavedGroups => Volatile.Read(ref savedGroups);
    public IReadOnlyDictionary<ChannelId, IReadOnlyList<ChannelPatchMembership>> MembershipIndex
        => Volatile.Read(ref membershipIndex);

    public PatchConfigurationRuntime(IPatchConfigurationPort port, IEnumerable<PatchGroupRuntimeDefinition> groups)
    {
        this.port = port ?? throw new ArgumentNullException(nameof(port));
        ArgumentNullException.ThrowIfNull(groups);
        this.groups = groups.Select(group => group with { Name = group.Name.Trim() })
            .Where(group => group.Name.Length > 0).ToArray();
        patchNames = this.groups.Where(group => !group.IsMultiSelect).Select(group => group.Name).ToArray();
        RefreshSnapshot();
    }

    public void SaveDefinition(string name, IEnumerable<PatchMemberAddress> members, bool enabled, bool oneWay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(members);
        string normalizedName = name.Trim();
        var normalizedMembers = members
            .Where(member => !string.IsNullOrWhiteSpace(member.SystemName) && member.DestinationId != 0)
            .Select(member => new PatchMemberAddress(member.SystemName, member.DestinationId, member.ChannelName))
            .GroupBy(member => member.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(member => new PatchMemberSetting
            {
                SystemName = member.SystemName,
                DestinationId = member.DestinationId,
                ChannelName = member.ChannelName
            }).ToList();
        CodeplugGroupState state = port.State;
        state.Memberships[normalizedName] = normalizedMembers;
        state.OneWayModes[normalizedName] = oneWay;
        state.EnabledStates[normalizedName] = enabled;
        RefreshSnapshot();
    }

    public void SetEnabled(string name, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        port.State.EnabledStates[name.Trim()] = enabled;
        RefreshSnapshot();
    }

    public void Restore(bool retainOnStartup)
    {
        if (retainOnStartup) Apply();
    }

    public void Apply()
    {
        CodeplugGroupState state = port.State;
        var memberships = PatchMembershipProjection.Capture(state, patchNames, port.CaptureChannels());
        port.ApplyMemberships(memberships, state.OneWayModes);
        RefreshSnapshot();
    }

    public void RefreshSnapshot()
    {
        CodeplugGroupState state = port.State;
        var resolver = new PatchTransmitChannelResolver(port.CaptureChannels());
        var index = new Dictionary<ChannelId, List<ChannelPatchMembership>>();
        var definitions = ImmutableList.CreateBuilder<ConsoleGroupDefinitionSnapshot>();
        foreach (PatchGroupRuntimeDefinition group in groups)
        {
            var configured = state.Memberships.GetValueOrDefault(group.Name) ?? [];
            var resolved = configured
                .Select(member => resolver.Resolve(new PatchMemberAddress(member.SystemName, member.DestinationId, member.ChannelName)))
                .ToArray();
            TransmitChannelDescriptor[] members = resolved.OfType<TransmitChannelDescriptor>().DistinctBy(channel => channel.Id).ToArray();
            bool enabled = group.IsMultiSelect || state.EnabledStates.GetValueOrDefault(group.Name);
            bool oneWay = !group.IsMultiSelect && state.OneWayModes.GetValueOrDefault(group.Name);
            definitions.Add(new(group.Name, group.IsMultiSelect, members.Select(channel => channel.Id).ToImmutableArray(),
                enabled, oneWay, resolved.Count(channel => channel is null)));
            for (int position = 0; position < members.Length; position++)
            {
                ChannelId id = members[position].Id;
                if (!index.TryGetValue(id, out var memberships)) index[id] = memberships = [];
                memberships.Add(new(PatchId.FromName(group.Name), group.Name, enabled, oneWay, oneWay && position == 0));
            }
        }
        Volatile.Write(ref savedGroups, definitions.ToImmutable());
        Volatile.Write(ref membershipIndex, index.ToImmutableDictionary(pair => pair.Key,
            pair => (IReadOnlyList<ChannelPatchMembership>)pair.Value.ToImmutableArray()));
        foreach (EventHandler observer in Changed?.GetInvocationList() ?? [])
        {
            try { observer(this, EventArgs.Empty); }
            catch
            {
                // A presentation observer cannot roll back saved operator state
                // or prevent other consumers from seeing the committed snapshot.
            }
        }
    }

}
