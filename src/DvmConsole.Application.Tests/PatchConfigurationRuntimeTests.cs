// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;
using DvmConsole.Core.Settings;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class PatchConfigurationRuntimeTests
{
    [Fact]
    public void SavedGroupSnapshotReportsUnresolvedMembersWithoutInventingChannels()
    {
        var port = new Port();
        var runtime = new PatchConfigurationRuntime(port, [new("Patch")]);
        runtime.SaveDefinition("Patch", [Address("Source", 100), Address("Missing", 999)], true, true);
        var saved = Assert.Single(runtime.SavedGroups);
        Assert.Equal(1, saved.UnresolvedMembers);
        Assert.Single(saved.Members);
        Assert.True(saved.SavedEnabled);
        Assert.True(saved.OneWay);
        runtime.SetEnabled("Patch", false);
        Assert.True(saved.SavedEnabled);
        Assert.False(Assert.Single(runtime.SavedGroups).SavedEnabled);
    }

    [Fact]
    public void SnapshotObserverFailureDoesNotInterruptSavingOrLaterObservers()
    {
        var port = new Port();
        var runtime = new PatchConfigurationRuntime(port, [new("Patch")]);
        int observed = 0;
        runtime.Changed += (_, _) => throw new InvalidOperationException("Observer fixture failure.");
        runtime.Changed += (_, _) => observed++;
        runtime.SaveDefinition("Patch", [Address("Source", 100)], true, false);
        Assert.Equal(1, observed);
        Assert.True(Assert.Single(runtime.MembershipIndex[port.Channels[0].Id]).IsEnabled);
        Assert.Single(port.State.Memberships["Patch"]);
    }

    [Fact]
    public void ImmutableMembershipSnapshotsPreserveSourceOrderDisabledGroupsAndMultiSelect()
    {
        var port = new Port();
        var runtime = new PatchConfigurationRuntime(port, [new("Patch"), new("Select", true)]);
        runtime.SaveDefinition("Patch", [Address("Target", 101), Address("Source", 100)], false, true);
        var original = runtime.MembershipIndex;
        ChannelId target = port.Channels[1].Id;
        ChannelPatchMembership patch = Assert.Single(original[target]);
        Assert.False(patch.IsEnabled);
        Assert.True(patch.IsOneWay);
        Assert.True(patch.IsSource);
        Assert.False(Assert.Single(original[port.Channels[0].Id]).IsSource);

        runtime.SaveDefinition("Select", [Address("Target", 101)], false, true);
        ChannelPatchMembership select = Assert.Single(runtime.MembershipIndex[target], item => item.Name == "Select");
        Assert.True(select.IsEnabled);
        Assert.False(select.IsOneWay);
        runtime.SetEnabled("Patch", true);
        Assert.True(runtime.MembershipIndex[target][0].IsEnabled);
        Assert.False(Assert.Single(original[target]).IsEnabled);
        Assert.Equal(0, port.Applies);
    }

    [Fact]
    public void StartupChoiceDoesNotPreventLaterExplicitRestoration()
    {
        var port = new Port();
        var runtime = new PatchConfigurationRuntime(port, [new("Patch")]);
        runtime.SaveDefinition("Patch", [Address("Source", 100), Address("Target", 101)], true, true);
        runtime.SaveDefinition("Multi-select", [Address("Source", 100), Address("Target", 101)], true, false);
        runtime.Restore(false);
        Assert.Equal(0, port.Applies);
        runtime.Apply();
        Assert.Equal(1, port.Applies);
        Assert.Equal("Patch", Assert.Single(port.Memberships).Key);
        Assert.Equal(new[] { "Source", "Target" }, port.Memberships["Patch"].Select(member => member.ChannelName));
        Assert.True(port.OneWay["Patch"]);
    }

    [Fact]
    public void SavedMembersKeepRoutingOrderAndStableChannelIdentity()
    {
        var port = new Port();
        var runtime = new PatchConfigurationRuntime(port, [new("Patch")]);
        runtime.SaveDefinition(" Patch ", [Address("Target", 101), Address("Target", 101), Address("Source", 100)], true, true);
        Assert.Equal(new[] { "Target", "Source" }, port.State.Memberships["Patch"].Select(member => member.ChannelName));
        Assert.Equal(0, port.Applies);
        runtime.Restore(true);
        Assert.Equal(new[] { "Target", "Source" }, port.Memberships["Patch"].Select(member => member.ChannelName));
        runtime.SetEnabled("Patch", false);
        runtime.Apply();
        Assert.Empty(port.Memberships);
        Assert.Equal(2, port.State.Memberships["Patch"].Count);
    }

    [Fact]
    public void ApplyReadsCurrentSettingsAndRejectsAmbiguousLegacyDestinations()
    {
        var port = new Port();
        var runtime = new PatchConfigurationRuntime(port, [new("Patch")]);
        runtime.SaveDefinition("Patch", [Address("Source", 100)], true, false);
        runtime.Apply();
        port.State = new CodeplugGroupState();
        port.Channels.Add(Channel("Other source", 100));
        runtime.SaveDefinition("Patch", [new PatchMemberAddress("System", 100), Address("Target", 101)], true, false);
        runtime.Apply();
        Assert.Equal("Target", Assert.Single(port.Memberships["Patch"]).ChannelName);
    }

    private static PatchMemberAddress Address(string name, uint destination) => new("System", destination, name);
    private static TransmitChannelDescriptor Channel(string name, uint destination)
    {
        var state = new ConsoleChannelState(new ChannelRuntimeDefinition(name, "System", "p25", destination, 0));
        return state.CaptureTransmitDescriptor(new ChannelConfigurationAccess(state.Runtime.Definition));
    }

    private sealed class Port : IPatchConfigurationPort
    {
        public CodeplugGroupState State { get; set; } = new();
        public List<TransmitChannelDescriptor> Channels { get; } = [Channel("Source", 100), Channel("Target", 101)];
        public IReadOnlyDictionary<string, IReadOnlyList<PatchMemberAddress>> Memberships { get; private set; }
            = new Dictionary<string, IReadOnlyList<PatchMemberAddress>>();
        public IReadOnlyDictionary<string, bool> OneWay { get; private set; } = new Dictionary<string, bool>();
        public int Applies { get; private set; }
        public IReadOnlyList<TransmitChannelDescriptor> CaptureChannels() => Channels;
        public void ApplyMemberships(IReadOnlyDictionary<string, IReadOnlyList<PatchMemberAddress>> memberships,
            IReadOnlyDictionary<string, bool> oneWay)
        {
            Applies++;
            Memberships = memberships;
            OneWay = oneWay;
        }
    }
}
