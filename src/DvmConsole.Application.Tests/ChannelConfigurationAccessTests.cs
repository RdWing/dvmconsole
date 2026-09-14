// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Configuration;
using DvmConsole.Core.Runtime;
using DvmConsole.Media;
using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ChannelConfigurationAccessTests
{
    [Fact]
    public void RuntimeKeyArrivalAndRemovalChangeSecureAdmissionWithoutDisablingListening()
    {
        using var keys = new P25KeyRing();
        var access = new ChannelConfigurationAccess(ChannelRuntimeDefinition.FromConfiguration(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "Test",
            Mode = "p25",
            Tgid = "100",
            Algo = "aes",
            KeyId = "0x50",
            SelectableEncryption = true
        }), keys);
        Assert.True(access.CanListen);
        Assert.False(access.CanTransmit(true));
        Assert.True(access.CanTransmit(false));
        Assert.True(access.CanToggleEncryption(true));
        Assert.False(access.CanToggleEncryption(false));
        keys.AddOrReplaceFromFne("Test", 0x84, 0x50, Convert.FromHexString("00112233445566778899AABBCCDDEEFF"));
        Assert.True(access.CanTransmit(true));
        Assert.True(access.CanToggleEncryption(false));
        keys.ClearFneKeys("Test");
        Assert.False(access.CanTransmit(true));
        Assert.True(access.CanListen);
    }

    [Fact]
    public void ReceiveOnlyRemainsUnavailableEvenWithClearTransmitSelected()
    {
        var access = new ChannelConfigurationAccess(ChannelRuntimeDefinition.FromConfiguration(new ChannelConfiguration
        {
            Name = "Monitor",
            System = "Test",
            Mode = "dmr",
            Tgid = "100",
            Slot = 2,
            RxOnly = true
        }));
        Assert.True(access.CanListen);
        Assert.False(access.CanTransmit(false));
        Assert.Equal("the channel is receive-only", access.TransmitUnavailableReason(false));
        Assert.Contains("TS2", access.AuthorityUnavailableReason);
    }
}
