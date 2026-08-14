using DvmConsole.Core.Configuration;
using DvmConsole.FneClient;
using Xunit;

namespace DvmConsole.FneClient.Tests;

public sealed class FneConnectionTests
{
    [Fact]
    public void MapsLegacySystemConfigurationToConnectionOptions()
    {
        var system = new SystemConfiguration
        {
            Name = "  Test FNE ",
            Identity = " Console ",
            Address = "127.0.0.1",
            Port = 62031,
            PeerId = 1000001,
            Password = "password",
            Encrypted = true,
            PresharedKey = "00112233445566778899AABBCCDDEEFF"
        };

        FneConnectionOptions options = FneConnectionOptions.FromConfiguration(system);

        Assert.Equal("Test FNE", options.Name);
        Assert.Equal("Console", options.Identity);
        Assert.Equal("127.0.0.1", options.Address);
        Assert.Equal(62031, options.Port);
        Assert.Equal((uint)1000001, options.PeerId);
        Assert.Equal("password", options.Password);
        Assert.Equal(system.PresharedKey, options.PresharedKey);
    }

    [Fact]
    public void DoesNotCarryEncryptionKeyWhenSystemIsClear()
    {
        var system = new SystemConfiguration
        {
            Name = "Clear FNE",
            Address = "127.0.0.1",
            Port = 62031,
            Encrypted = false,
            PresharedKey = "not-used"
        };

        FneConnectionOptions options = FneConnectionOptions.FromConfiguration(system);

        Assert.Null(options.PresharedKey);
    }

    [Fact]
    public async Task StartsDisconnectedWithoutOpeningNetworkSocket()
    {
        var options = new FneConnectionOptions("Test", "Test", "127.0.0.1", 62031, 1, null, false, null);
        await using var connection = new FneConnection(options);

        Assert.Equal(FneConnectionState.Disconnected, connection.Status.State);
        Assert.Null(connection.Peer);
    }

    [Fact]
    public void ExposesConnectedState()
    {
        Assert.Contains(FneConnectionState.Connected, Enum.GetValues<FneConnectionState>());
    }

    [Fact]
    public void TrafficFrameOwnsPayloadAndKeepsProtocolMetadata()
    {
        byte[] payload = { 1, 2, 3 };
        var frame = new FneTrafficFrame(
            FneTrafficProtocol.Dmr,
            10,
            20,
            30,
            2,
            "GROUP",
            "VOICE",
            "BURST",
            4,
            5,
            payload);

        payload[0] = 99;

        Assert.Equal(FneTrafficProtocol.Dmr, frame.Protocol);
        Assert.Equal((byte)2, frame.Slot);
        Assert.Equal((byte)1, frame.Payload[0]);
        Assert.Equal((uint)5, frame.StreamId);
    }
}
