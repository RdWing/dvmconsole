using DvmConsole.Core.Configuration;
using DvmConsole.FneClient;
using System.Net;
using Xunit;

namespace DvmConsole.FneClient.Tests;

public sealed class FneConnectionTests
{
    [Fact]
    public async Task TrafficSubscribersRemainOrderedAndFailureIsolated()
    {
        await using var connection = new FneConnection(new FneConnectionOptions(
            "Test", "Test", "127.0.0.1", 62031, 1, null, false, null));
        var calls = new List<string>();
        EventHandler<FneTrafficFrame> failing = (_, _) =>
        {
            calls.Add("first");
            throw new InvalidOperationException("expected test failure");
        };
        EventHandler<FneTrafficFrame> succeeding = (_, _) => calls.Add("second");
        connection.TrafficReceived += failing;
        connection.TrafficReceived += succeeding;

        connection.PublishTraffic(new FneTrafficFrame(
            FneTrafficProtocol.P25,
            1,
            2,
            3,
            null,
            "GROUP",
            "VOICE",
            "LDU1",
            1,
            77,
            []));
        connection.TrafficReceived -= succeeding;

        Assert.Equal(["first", "second"], calls);
    }

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

        using IFnePeerSession session = connection.CreatePeerSession(new IPEndPoint(IPAddress.Loopback, 62031));
        fnecore.FnePeer peer = session.Peer;

        Assert.Equal("TYF_OP1", peer.Information.Details.Identity);
        Assert.Equal(FneConnection.SoftwareIdentifier, peer.Information.Details.Software);
        Assert.Equal("DVMC_NEO_0.4.2", peer.Information.Details.Software);
        Assert.Equal(options.PeerId, peer.Information.PeerID);
        Assert.Equal(fnecore.ConnectionState.WAITING_LOGIN, peer.Information.State);
        Assert.Equal(fnecore.LogLevel.DEBUG, peer.LogLevel);
        Assert.False(peer.RawPacketTrace);
    }

    [Theory]
    [InlineData("0.1.0", "DVMC_NEO_0.1.0")]
    [InlineData("0.2.1-beta.1+abcdef123456", "DVMC_NEO_0.2.1-beta.1")]
    [InlineData(null, "DVMC_NEO_UNKNOWN")]
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
        using IFnePeerSession session = connection.CreatePeerSession(new IPEndPoint(IPAddress.Loopback, 62031));
        fnecore.FnePeer peer = session.Peer;
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
    public async Task ProtocolErrorLogDoesNotMasqueradeAsConnectionLoss()
    {
        var options = new FneConnectionOptions(
            "Test FNE", "TYF_OP1", "127.0.0.1", 62031, 1000001, null, false, null);
        await using var connection = new FneConnection(options);
        using IFnePeerSession session = connection.CreatePeerSession(new IPEndPoint(IPAddress.Loopback, 62031));
        fnecore.FnePeer peer = session.Peer;

        peer.Logger(fnecore.LogLevel.ERROR, "Unknown master opcode 7F / 00");

        Assert.Equal(FneConnectionState.Disconnected, connection.Status.State);
    }

    [Fact]
    public async Task ExplicitSocketErrorStillPublishesConnectionFault()
    {
        var options = new FneConnectionOptions(
            "Test FNE", "TYF_OP1", "127.0.0.1", 62031, 1000001, null, false, null);
        await using var connection = new FneConnection(options);
        using IFnePeerSession session = connection.CreatePeerSession(new IPEndPoint(IPAddress.Loopback, 62031));
        fnecore.FnePeer peer = session.Peer;

        peer.Logger(fnecore.LogLevel.ERROR, "SOCKET ERROR: connection reset");

        Assert.Equal(FneConnectionState.Faulted, connection.Status.State);
    }

    [Fact]
    public void StateMonitorRepublishesAuthoritativeStateAfterTransientOverride()
    {
        Assert.True(FneConnection.ShouldPublishMonitoredState(
            FneConnectionState.Connected,
            FneConnectionState.Connected,
            FneConnectionState.Faulted));
        Assert.False(FneConnection.ShouldPublishMonitoredState(
            FneConnectionState.Connected,
            FneConnectionState.Connected,
            FneConnectionState.Connected));
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
    public async Task LoginRetriesBackOffAndSuccessfulConnectionRestoresNormalCadence()
    {
        var sessions = new RecordingPeerSessionFactory();
        await using var connection = new FneConnection(
            new FneConnectionOptions("Test", "Test", "127.0.0.1", 62031, 1, null, false, null),
            TimeProvider.System,
            new LoopbackEndpointResolver(),
            sessions);
        var messages = new List<string>();
        connection.LogReceived += (_, entry) => messages.Add(entry.Message);

        await connection.StartAsync();
        RecordingPeerSession session = sessions.Single();
        int[] expectedIntervals = [5, 10, 20, 40, 60, 60];

        foreach (int expectedInterval in expectedIntervals)
        {
            session.Callbacks.Log(fnecore.LogLevel.INFO, "Sending login request to MASTER");
            Assert.Equal(expectedInterval, session.Peer.PingTime);
        }

        Assert.Contains(messages, message => message.Contains(
            "next retry in 60 seconds",
            StringComparison.Ordinal));

        session.Peer.Information.State = fnecore.ConnectionState.RUNNING;
        session.Callbacks.Connected(
            session.Peer,
            new fnecore.PeerConnectedEvent(1, session.Peer.Information));
        Assert.Equal(FnePeerSessionFactory.DefaultPingIntervalSeconds, session.Peer.PingTime);

        session.Callbacks.Log(fnecore.LogLevel.INFO, "Sending login request to MASTER");
        Assert.Equal(FnePeerSessionFactory.DefaultPingIntervalSeconds, session.Peer.PingTime);
    }

    [Fact]
    public async Task StalledHandshakeRecyclesOnlyItsPeerSession()
    {
        var sessions = new RecordingPeerSessionFactory();
        await using var connection = new FneConnection(
            new FneConnectionOptions("Test", "Test", "127.0.0.1", 62031, 1, null, false, null),
            TimeProvider.System,
            new LoopbackEndpointResolver(),
            sessions,
            TimeSpan.FromMilliseconds(40));

        await connection.StartAsync();
        RecordingPeerSession first = sessions.Single();
        first.Peer.Information.State = fnecore.ConnectionState.WAITING_AUTHORISATION;
        first.Callbacks.Log(fnecore.LogLevel.INFO, "login ACK received");

        await WaitUntilAsync(() => sessions.Count == 2);

        Assert.True(first.IsStopped);
        Assert.True(sessions.Latest().IsStarted);
        Assert.Equal(FneConnectionState.WaitingForLogin, connection.Status.State);
    }

    [Fact]
    public async Task CompletedHandshakeCancelsSessionRecycle()
    {
        var sessions = new RecordingPeerSessionFactory();
        await using var connection = new FneConnection(
            new FneConnectionOptions("Test", "Test", "127.0.0.1", 62031, 1, null, false, null),
            TimeProvider.System,
            new LoopbackEndpointResolver(),
            sessions,
            TimeSpan.FromMilliseconds(80));

        await connection.StartAsync();
        RecordingPeerSession first = sessions.Single();
        first.Peer.Information.State = fnecore.ConnectionState.WAITING_AUTHORISATION;
        first.Callbacks.Log(fnecore.LogLevel.INFO, "login ACK received");
        first.Peer.Information.State = fnecore.ConnectionState.RUNNING;
        first.Callbacks.Connected(
            first.Peer,
            new fnecore.PeerConnectedEvent(1, first.Peer.Information));

        await Task.Delay(200);

        Assert.False(fnecore.FnePeerKeepaliveStreamInitializer.TryInitialize(first.Peer));
        Assert.Equal(1, sessions.Count);
        Assert.False(first.IsStopped);
        Assert.Equal(FneConnectionState.Connected, connection.Status.State);
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
    public async Task RejectsUnsolicitedP25KeyResponse()
    {
        await using var connection = new FneConnection(new FneConnectionOptions(
            "Test", "Test", "127.0.0.1", 62031, 1, null, false, null));
        int published = 0;
        connection.KeyResponseReceived += (_, _) => published++;

        bool accepted = connection.TryPublishRequestedP25KeyResponse(
            fnecore.P25.P25Defines.P25_ALGO_AES,
            0x50,
            new byte[32]);

        Assert.False(accepted);
        Assert.Equal(0, published);
    }

    [Fact]
    public async Task AcceptsOnlyTheMatchingRequestedP25KeyResponseOnce()
    {
        await using var connection = new FneConnection(new FneConnectionOptions(
            "Test", "Test", "127.0.0.1", 62031, 1, null, false, null));
        FneKeyResponse? received = null;
        connection.KeyResponseReceived += (_, response) => received = response;
        connection.RegisterPendingP25KeyRequest(fnecore.P25.P25Defines.P25_ALGO_AES, 0x50);

        Assert.False(connection.TryPublishRequestedP25KeyResponse(
            fnecore.P25.P25Defines.P25_ALGO_DES,
            0x50,
            new byte[8]));
        Assert.False(connection.TryPublishRequestedP25KeyResponse(
            fnecore.P25.P25Defines.P25_ALGO_AES,
            0x51,
            new byte[32]));
        Assert.True(connection.TryPublishRequestedP25KeyResponse(
            fnecore.P25.P25Defines.P25_ALGO_AES,
            0x50,
            new byte[32]));
        Assert.False(connection.TryPublishRequestedP25KeyResponse(
            fnecore.P25.P25Defines.P25_ALGO_AES,
            0x50,
            new byte[32]));

        Assert.NotNull(received);
        Assert.Equal((ushort)0x50, received!.KeyId);
        Assert.Equal(fnecore.P25.P25Defines.P25_ALGO_AES, received.AlgorithmId);
    }

    [Fact]
    public async Task RejectsExpiredP25KeyResponse()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        await using var connection = new FneConnection(
            new FneConnectionOptions("Test", "Test", "127.0.0.1", 62031, 1, null, false, null),
            timeProvider);
        connection.RegisterPendingP25KeyRequest(fnecore.P25.P25Defines.P25_ALGO_AES, 0x50);
        timeProvider.Advance(FneConnection.P25KeyResponseWindow + TimeSpan.FromTicks(1));

        Assert.False(connection.TryPublishRequestedP25KeyResponse(
            fnecore.P25.P25Defines.P25_ALGO_AES,
            0x50,
            new byte[32]));
    }

    [Fact]
    public async Task InvalidP25KeyMaterialDoesNotConsumeMatchingRequest()
    {
        await using var connection = new FneConnection(new FneConnectionOptions(
            "Test", "Test", "127.0.0.1", 62031, 1, null, false, null));
        connection.RegisterPendingP25KeyRequest(fnecore.P25.P25Defines.P25_ALGO_AES, 0x50);

        Assert.False(connection.TryPublishRequestedP25KeyResponse(
            fnecore.P25.P25Defines.P25_ALGO_AES,
            0x50,
            ReadOnlyMemory<byte>.Empty));
        Assert.False(connection.TryPublishRequestedP25KeyResponse(
            fnecore.P25.P25Defines.P25_ALGO_AES,
            0x50,
            new byte[33]));
        Assert.True(connection.TryPublishRequestedP25KeyResponse(
            fnecore.P25.P25Defines.P25_ALGO_AES,
            0x50,
            new byte[32]));
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

    private sealed class ManualTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = initialUtcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow = utcNow.Add(duration);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
            await Task.Delay(10, timeout.Token);
    }

    private sealed class LoopbackEndpointResolver : IFneEndpointResolver
    {
        public Task<IPEndPoint> ResolveAsync(
            string address,
            int port,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new IPEndPoint(IPAddress.Loopback, port));
        }
    }

    private sealed class RecordingPeerSessionFactory : IFnePeerSessionFactory
    {
        private readonly object sync = new();
        private readonly List<RecordingPeerSession> sessions = [];

        public int Count
        {
            get
            {
                lock (sync)
                    return sessions.Count;
            }
        }

        public IFnePeerSession Create(
            FneConnectionOptions options,
            IPEndPoint endpoint,
            string softwareIdentifier,
            FnePeerSessionCallbacks callbacks)
        {
            var session = new RecordingPeerSession(
                new fnecore.FnePeer("TEST", options.PeerId, endpoint),
                callbacks);
            lock (sync)
                sessions.Add(session);
            return session;
        }

        public RecordingPeerSession Single()
        {
            lock (sync)
                return Assert.Single(sessions);
        }

        public RecordingPeerSession Latest()
        {
            lock (sync)
                return sessions[^1];
        }
    }

    private sealed class RecordingPeerSession(
        fnecore.FnePeer peer,
        FnePeerSessionCallbacks callbacks) : IFnePeerSession
    {
        public fnecore.FnePeer Peer { get; } = peer;
        public FnePeerSessionCallbacks Callbacks { get; } = callbacks;
        public bool IsStarted { get; private set; }
        public bool IsStopped { get; private set; }

        public void Start() => IsStarted = true;

        public void Stop()
        {
            IsStopped = true;
            IsStarted = false;
        }

        public void Dispose() => Stop();
    }
}
