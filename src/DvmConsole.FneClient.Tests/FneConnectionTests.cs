using DvmConsole.Core.Configuration;
using DvmConsole.FneClient;
using System.Net;
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
            Rid = "1001",
            Password = "password",
            Encrypted = true,
            TransportEncryptionMode = "cbc",
            PresharedKey = "00112233445566778899AABBCCDDEEFF"
        };

        FneConnectionOptions options = FneConnectionOptions.FromConfiguration(system);

        Assert.Equal("Test FNE", options.Name);
        Assert.Equal("Console", options.Identity);
        Assert.Equal("127.0.0.1", options.Address);
        Assert.Equal(62031, options.Port);
        Assert.Equal((uint)1000001, options.PeerId);
        Assert.Equal((uint)1001, options.SourceId);
        Assert.Equal("password", options.Password);
        Assert.Equal(system.PresharedKey, options.PresharedKey);
        Assert.Equal(FneTransportEncryptionPreference.Cbc, options.TransportEncryptionMode);
        Assert.Null(options.KmfPresharedKey);
    }

    [Fact]
    public async Task CreatedPeerCarriesConfiguredIdentityIntoFneInformation()
    {
        var options = new FneConnectionOptions(
            "Test FNE",
            "TYF_OP1",
            "127.0.0.1",
            62031,
            1000001,
            "password",
            false,
            null);
        await using var connection = new FneConnection(options);

        fnecore.FnePeer peer = connection.CreatePeer(new IPEndPoint(IPAddress.Loopback, 62031));

        Assert.Equal("TYF_OP1", peer.Information.Details.Identity);
        Assert.Equal(FneConnection.SoftwareIdentifier, peer.Information.Details.Software);
        Assert.Equal("DVMC_AV_0.2.3", peer.Information.Details.Software);
        Assert.Equal(options.PeerId, peer.Information.PeerID);
        Assert.Equal(fnecore.ConnectionState.WAITING_LOGIN, peer.Information.State);
        Assert.Equal(fnecore.LogLevel.DEBUG, peer.LogLevel);
        Assert.False(peer.RawPacketTrace);
    }

    [Theory]
    [InlineData("0.1.0", "DVMC_AV_0.1.0")]
    [InlineData("0.2.1-beta.1+abcdef123456", "DVMC_AV_0.2.1-beta.1")]
    [InlineData(null, "DVMC_AV_UNKNOWN")]
    public void FormatsVersionedFneSoftwareIdentifier(string? informationalVersion, string expected)
        => Assert.Equal(expected, FneConnection.FormatSoftwareIdentifier(informationalVersion));

    [Fact]
    public async Task PublishesRedactedPeerDiagnostics()
    {
        var options = new FneConnectionOptions(
            "Test FNE",
            "TYF_OP1",
            "127.0.0.1",
            62031,
            1000001,
            null,
            false,
            null)
        {
            EnableDiagnostics = true
        };
        await using var connection = new FneConnection(options);
        fnecore.FnePeer peer = connection.CreatePeer(new IPEndPoint(IPAddress.Loopback, 62031));
        FneLogEntry? received = null;
        connection.LogReceived += (_, entry) => received = entry;

        peer.Logger(fnecore.LogLevel.DEBUG, "Network Received (from 127.0.0.1) -- DUMP 0000: secret");

        Assert.NotNull(received);
        Assert.Equal("Test FNE", received!.SystemName);
        Assert.Equal("DEBUG", received.Severity.ToString().ToUpperInvariant());
        Assert.DoesNotContain("secret", received.Message);
        Assert.Contains("payload redacted", received.Message);
    }

    [Fact]
    public void KeepsKmfKeySeparateFromTransportKey()
    {
        var system = new SystemConfiguration
        {
            Name = "KMM FNE",
            Address = "127.0.0.1",
            Port = 62031,
            PresharedKey = "transport",
            Encrypted = true,
            KmfPresharedKey = "kmf"
        };

        FneConnectionOptions options = FneConnectionOptions.FromConfiguration(system);

        Assert.Equal("transport", options.PresharedKey);
        Assert.Equal("kmf", options.KmfPresharedKey);
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
    public async Task ReconnectHonorsCancellationBeforeOpeningNetworkSocket()
    {
        var options = new FneConnectionOptions("Test", "Test", "127.0.0.1", 62031, 1, null, false, null);
        await using var connection = new FneConnection(options);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            connection.StartOrReconnectAsync(cancellation.Token));
        Assert.Null(connection.Peer);
        Assert.Equal(FneConnectionState.Disconnected, connection.Status.State);
    }

    [Fact]
    public async Task RejectsP25KeyRequestBeforeConnectionStarts()
    {
        var options = new FneConnectionOptions("Test", "Test", "127.0.0.1", 62031, 1, null, false, null)
        {
            SourceId = 1001
        };
        await using var connection = new FneConnection(options);

        Assert.Throws<InvalidOperationException>(() => connection.RequestP25Key(fnecore.P25.P25Defines.P25_ALGO_AES, 0x50));
    }

    [Fact]
    public async Task RejectsP25SubscriberCommandBeforeConnectionStarts()
    {
        var options = new FneConnectionOptions("Test", "Test", "127.0.0.1", 62031, 1, null, false, null)
        {
            SourceId = 1001
        };
        await using var connection = new FneConnection(options);

        Assert.Throws<InvalidOperationException>(() => connection.SendP25SubscriberCommand(
            P25SubscriberCommand.RadioCheck,
            2002));
    }

    [Fact]
    public async Task RejectsUnsupportedP25KeyRequest()
    {
        await using var connection = new FneConnection(new FneConnectionOptions("Test", "Test", "127.0.0.1", 62031, 1, null, false, null));

        Assert.Throws<ArgumentOutOfRangeException>(() => connection.RequestP25Key(0x12, 0x50));
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
