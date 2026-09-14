// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Configuration;
using DvmConsole.Core.Runtime;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ConsoleChannelSnapshotProjectorTests
{
    private static ConsoleChannelState Channel(string name = "Dispatch")
        => new(new ChannelRuntimeDefinition(name, "System", "p25", 100, 0));

    [Fact]
    public void TransmitDescriptorReadsCurrentSharedIntentAndKeepsEarlierCaptureStable()
    {
        var channel = new ConsoleChannelState(new ChannelRuntimeDefinition("Secure", "System", "p25", 100, 0,
            encryptionAlgorithm: "aes256", encryptionKeyId: "1", selectableEncryption: true));
        var access = new ChannelConfigurationAccess(channel.Runtime.Definition);
        TransmitChannelDescriptor secured = channel.CaptureTransmitDescriptor(access);
        Assert.True(secured.TransmitEncrypted);
        Assert.False(secured.CanTransmitByConfiguration);
        channel.Operator.SetTransmitEncrypted(false);
        channel.Operator.SetHasCallPriority(true);
        channel.Operator.SetAudioEnabled(true);
        channel.Runtime.MarkReceiving(42, 10);
        TransmitChannelDescriptor clear = channel.CaptureTransmitDescriptor(access);
        Assert.False(clear.TransmitEncrypted);
        Assert.True(clear.CanTransmitByConfiguration);
        Assert.True(clear.ReceiveActive);
        Assert.True(clear.AllowsTransmitDuringReceive);
        Assert.True(secured.TransmitEncrypted);
        Assert.False(secured.ReceiveActive);
    }

    [Fact]
    public void CapturesRuntimeWithoutPresentationAndFreezesPatchMembership()
    {
        var channel = Channel();
        var aliases = new RadioAliasIndex([new RadioAlias { Rid = 42, Alias = " Unit 42 " }]);
        channel.Operator.SetAudioEnabled(true);
        channel.Operator.SetRecordingEnabled(true);
        channel.Runtime.MarkReceiving(42, 10);
        channel.SetAuthority(TargetAuthorityState.Unavailable);
        var patches = new List<ChannelPatchMembership>
        { new(PatchId.FromName("Dispatch"), "Dispatch", true, true, true) };
        ChannelControlSnapshot captured = ConsoleChannelSnapshotProjector.Capture(channel, aliases,
            new ChannelConfigurationAccess(channel.Runtime.Definition),
            new(Recording: true, EffectiveMuteReason: "Muted", Patches: patches));
        patches.Clear();
        channel.Runtime.MarkIdle();
        channel.Operator.SetAudioEnabled(false);
        Assert.Equal(ChannelRuntimeState.Receiving, captured.RuntimeState);
        Assert.True(captured.ReceiveActive);
        Assert.True(captured.TarArmed);
        Assert.True(captured.Recording);
        Assert.Equal("Unit 42", captured.LastCaller);
        Assert.Equal(42u, captured.LastCallerSourceId);
        var idle = ConsoleChannelSnapshotProjector.Capture(channel, aliases,
            new ChannelConfigurationAccess(channel.Runtime.Definition), new());
        Assert.Equal(42u, idle.LastCallerSourceId);
        Assert.False(captured.HasSameContent(captured with { LastCallerSourceId = 43 }));
        Assert.Contains("stream 10", captured.StateText);
        Assert.Equal("the FNE does not allow TG 100", captured.AuthorityReason);
        Assert.Equal("Muted", captured.EffectiveMuteReason);
        Assert.Single(captured.Patches);
    }

    [Fact]
    public void SharedStateTextPreservesTransmitAndSuspensionPrecedence()
    {
        var channel = Channel();
        var aliases = new RadioAliasIndex(null);
        channel.Operator.SetAudioEnabled(true);
        channel.Runtime.MarkReceiving(42, 10);
        channel.Operator.SetAudioSuspended(true);
        Assert.Equal("RX muted during console transmit", ConsoleChannelSnapshotProjector.StateText(channel, aliases));
        channel.Operator.SetTransmitTransition(true, false);
        Assert.Equal("Starting PTT…", ConsoleChannelSnapshotProjector.StateText(channel, aliases));
        channel.Operator.SetTransmitTransition(false, true);
        Assert.Equal("Releasing PTT…", ConsoleChannelSnapshotProjector.StateText(channel, aliases));
        channel.Operator.SetTransmitTransition(false, false);
        channel.Runtime.MarkTransmitting(20);
        Assert.Equal(channel.Runtime.StateText, ConsoleChannelSnapshotProjector.StateText(channel, aliases));
        Assert.Equal("42", ConsoleChannelSnapshotProjector.LastCallerText(channel, aliases));
    }

    [Fact]
    public void TransmitTransitionChangesSnapshotContentWithoutParsingStatusText()
    {
        var channel = Channel();
        var aliases = new RadioAliasIndex(null);
        var access = new ChannelConfigurationAccess(channel.Runtime.Definition);
        ChannelControlSnapshot Capture() => ConsoleChannelSnapshotProjector.Capture(channel, aliases, access, new());
        ChannelControlSnapshot idle = Capture();
        channel.Operator.SetTransmitTransition(true, false);
        ChannelControlSnapshot starting = Capture();
        Assert.True(starting.TransmitStarting);
        Assert.False(starting.TransmitStopping);
        Assert.False(idle.HasSameContent(starting with { StateText = idle.StateText }));
        channel.Operator.SetTransmitTransition(false, true);
        ChannelControlSnapshot stopping = Capture();
        Assert.False(stopping.TransmitStarting);
        Assert.True(stopping.TransmitStopping);
        Assert.False(idle.HasSameContent(stopping with { StateText = idle.StateText }));
        Assert.False(idle.TransmitStarting);
    }

    [Fact]
    public void EquivalentChannelUsesAudiblePeerIdentityWithoutChangingLocalLastCaller()
    {
        var first = Channel("First");
        var second = Channel("Second");
        ConsoleChannelState.LinkReceivePeers([first, second]);
        first.Operator.SetAudioEnabled(true);
        second.Operator.SetAudioEnabled(true);
        first.Receive.TryBeginPlayback(42, 10);
        var aliases = new RadioAliasIndex([new RadioAlias { Rid = 42, Alias = "Unit" }]);
        Assert.Equal("Receiving from Unit (42) (stream 10)", ConsoleChannelSnapshotProjector.StateText(second, aliases));
        Assert.Equal("--", ConsoleChannelSnapshotProjector.LastCallerText(second, aliases));
        second.Operator.SetAudioEnabled(false);
        Assert.Equal(second.Runtime.StateText, ConsoleChannelSnapshotProjector.StateText(second, aliases));
    }
}
