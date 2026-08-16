using DvmConsole.Core.Configuration;
using DvmConsole.Media;
using fnecore.P25;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class KeyStatusItemViewModelTests
{
    [Fact]
    public void ShowsRedactedIdentifiersAndAvailableStatus()
    {
        using var resolver = new P25KeyRing("Alpha", new KeyContainer
        {
            Keys =
            [
                new KeyEntry
                {
                    KeyId = 0x50,
                    AlgId = P25Defines.P25_ALGO_AES,
                    Key = "00112233445566778899AABBCCDDEEFF"
                }
            ]
        });
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Secure Dispatch",
            System = "Alpha",
            Tgid = "101",
            Mode = "p25",
            Algo = "aes",
            KeyId = "0x50"
        }, resolver);

        KeyStatusItemViewModel item = KeyStatusItemViewModel.From(channel, resolver);

        Assert.Equal("Alpha", item.SystemName);
        Assert.Equal("Secure Dispatch", item.ChannelName);
        Assert.Equal("P25", item.ModeText);
        Assert.Equal("0x84", item.AlgorithmIdText);
        Assert.Equal("0x0050", item.KeyIdText);
        Assert.Equal("Available · local file", item.StatusText);
        Assert.DoesNotContain("001122", item.AlgorithmIdText + item.KeyIdText + item.StatusText);

        resolver.AddOrReplaceFromFne(
            "Alpha",
            P25Defines.P25_ALGO_AES,
            0x50,
            Convert.FromHexString("FFEEDDCCBBAA99887766554433221100FFEEDDCCBBAA99887766554433221100"));

        Assert.Equal("Available · FNE/KMM", KeyStatusItemViewModel.From(channel, resolver).StatusText);
    }

    [Fact]
    public void MarksMissingAndUnsupportedKeysWithoutClaimingAvailability()
    {
        var p25Channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Missing Secure",
            System = "Alpha",
            Tgid = "102",
            Mode = "p25",
            Algo = "des-ofb",
            KeyId = "81"
        }, new P25KeyRing());
        var dmrChannel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Encrypted DMR",
            System = "Alpha",
            Tgid = "103",
            Mode = "dmr",
            Slot = 1,
            Algo = "aes",
            KeyId = "0x50"
        });

        Assert.Equal("Key unavailable", KeyStatusItemViewModel.From(p25Channel, new P25KeyRing()).StatusText);
        Assert.Equal("Unsupported protocol", KeyStatusItemViewModel.From(dmrChannel, null).StatusText);
    }
}
