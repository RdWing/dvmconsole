// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;
using DvmConsole.Media;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ConsoleTransmitChannelDirectoryTests
{
    [Fact]
    public void ManualSelectionRetainsUnavailableIntentAndHonorsScopeOrder()
    {
        var first = new ConsoleChannelState(new ChannelRuntimeDefinition("First", "Alpha", "p25", 100, 0));
        var second = new ConsoleChannelState(new ChannelRuntimeDefinition("Second", "Beta", "p25", 101, 0));
        var directory = new ConsoleTransmitChannelDirectory([first, second]);
        first.Operator.SetTransmitSelected(true);
        second.Operator.SetTransmitSelected(true);
        second.SetAuthority(TargetAuthorityState.Unavailable);
        Assert.True(directory.CanTransmitByConfiguration(second.Id));
        Assert.False(directory.CanTransmit(second.Id));
        Assert.Equal([second.Id, first.Id], directory.SelectManualChannels([second.Id, first.Id, second.Id]));
        Assert.Equal([first.Id], directory.SelectManualChannels([first.Id]));
        first.Operator.SetTransmitSelected(false);
        Assert.Equal([second.Id], directory.SelectManualChannels());
        second.SetAuthority(TargetAuthorityState.Available);
        Assert.True(directory.CanTransmit(second.Id));
    }

    [Fact]
    public void ShutdownIncludesCaptureOwnersAndRetainedTransmitStateExactlyOnce()
    {
        var captured = new ConsoleChannelState(new ChannelRuntimeDefinition("Captured", "Test", "p25", 100, 0));
        var retained = new ConsoleChannelState(new ChannelRuntimeDefinition("Retained", "Test", "p25", 101, 0));
        var idle = new ConsoleChannelState(new ChannelRuntimeDefinition("Idle", "Test", "p25", 102, 0));
        var directory = new ConsoleTransmitChannelDirectory([captured, retained, idle]);
        captured.SetTransmitEnabled(true, 1);
        retained.SetTransmitEnabled(true, 2);
        Assert.Equal([captured.Id, retained.Id], directory.CaptureShutdownChannels([captured.Id, captured.Id]));
        retained.SetTransmitEnabled(false);
        Assert.Equal([captured.Id], directory.CaptureShutdownChannels([]));
    }

    [Fact]
    public void DisconnectOwnershipIncludesRetainedStateButExcludesOtherSystems()
    {
        var first = new ConsoleChannelState(new ChannelRuntimeDefinition("First", "Alpha", "p25", 100, 0));
        var second = new ConsoleChannelState(new ChannelRuntimeDefinition("Second", "Beta", "p25", 101, 0));
        var directory = new ConsoleTransmitChannelDirectory([first, second]);
        Assert.False(directory.OwnsActiveTransmit([first.Id], [second.Id]));
        Assert.True(directory.OwnsActiveTransmit([first.Id], [first.Id]));
        first.SetTransmitEnabled(true, 1);
        Assert.True(directory.OwnsActiveTransmit([first.Id], []));
        first.SetTransmitEnabled(false);
        Assert.False(directory.OwnsActiveTransmit([first.Id], []));
    }

    [Fact]
    public void CapturesCurrentEncryptionKeysAndOperatorStateWithoutRebuildingTheDirectory()
    {
        using var keys = new P25KeyRing();
        var channel = new ConsoleChannelState(new ChannelRuntimeDefinition(
            "Dispatch", "Test", "p25", 100, 0, encryptionAlgorithm: "aes",
            encryptionKeyId: "0x50", selectableEncryption: true));
        var directory = new ConsoleTransmitChannelDirectory([channel], keys);
        var beforeKey = directory.Capture(channel.Id);
        Assert.False(beforeKey.CanTransmitByConfiguration);
        keys.AddOrReplaceFromFne("Test", 0x84, 0x50,
            Convert.FromHexString("00112233445566778899AABBCCDDEEFF"));
        Assert.True(directory.Find(channel.Id)!.CanTransmitByConfiguration);
        Assert.True(directory.CapturePatchCapabilities(channel.Id).CanTransmit);
        channel.SetAuthority(TargetAuthorityState.Unavailable);
        Assert.False(directory.CapturePatchCapabilities(channel.Id).CanTransmit);
        Assert.True(directory.CapturePatchCapabilities(channel.Id).CanReceive);
        channel.SetAuthority(TargetAuthorityState.Available);
        keys.ClearFneKeys("Test");
        Assert.False(directory.Capture(channel.Id).CanTransmitByConfiguration);
        channel.Operator.SetTransmitEncrypted(false);
        var clear = Assert.Single(directory.CaptureAll());
        Assert.True(clear.CanTransmitByConfiguration);
        Assert.False(clear.TransmitEncrypted);
        Assert.True(beforeKey.TransmitEncrypted);
        Assert.False(beforeKey.CanTransmitByConfiguration);
    }

    [Fact]
    public void RetainsSessionMembershipAndOrderAndRejectsUnknownChannels()
    {
        var first = new ConsoleChannelState(new ChannelRuntimeDefinition("First", "Test", "p25", 100, 0));
        var second = new ConsoleChannelState(new ChannelRuntimeDefinition("Second", "Test", "dmr", 200, 0, rxOnly: true));
        var source = new List<ConsoleChannelState> { second, first };
        var directory = new ConsoleTransmitChannelDirectory(source);
        source.Clear();
        var captured = directory.CaptureAll();
        Assert.Equal([second.Id, first.Id], captured.Select(channel => channel.Id));
        Assert.False(captured[0].CanTransmitByConfiguration);
        var unknown = new ConsoleChannelState(new ChannelRuntimeDefinition("Other", "Test", "nxdn", 300, 0)).Id;
        Assert.Null(directory.Find(unknown));
        Assert.Throws<KeyNotFoundException>(() => directory.Capture(unknown));
    }
}
