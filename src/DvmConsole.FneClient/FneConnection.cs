using System.Net;
using System.Net.Sockets;
using DvmConsole.Core.Configuration;
using fnecore;
using fnecore.P25;
using fnecore.P25.KMM;

namespace DvmConsole.FneClient;

public enum FneConnectionState
{
    Disconnected,
    Starting,
    WaitingForLogin,
    Authenticating,
    Configuring,
    Connected,
    Stopping,
    Faulted
}

public sealed record FneConnectionOptions(
    string Name,
    string Identity,
    string Address,
    int Port,
    uint PeerId,
    string? Password,
    bool Encrypted,
    string? PresharedKey)
{
    /// <summary>
    /// Radio/source ID used for outbound voice traffic. It is optional so
    /// connections without a transmit RID can still be used for receive-only
    /// monitoring.
    /// </summary>
    public uint? SourceId { get; init; }

    /// <summary>
    /// Optional KMF key used only to decrypt peer-encrypted P25 KMM responses.
    /// It is never inferred from the FNE transport preshared key.
    /// </summary>
    public string? KmfPresharedKey { get; init; }

    /// <summary>
    /// Enables sanitized diagnostic callbacks used by the bounded live probe.
    /// Raw packet contents are never exposed by the rebuild client.
    /// </summary>
    public bool EnableDiagnostics { get; init; }

    public static FneConnectionOptions FromConfiguration(SystemConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (string.IsNullOrWhiteSpace(configuration.Name))
            throw new ArgumentException("FNE system name is required.", nameof(configuration));
        if (string.IsNullOrWhiteSpace(configuration.Address))
            throw new ArgumentException("FNE system address is required.", nameof(configuration));
        if (configuration.Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(configuration), "FNE system port must be between 1 and 65535.");

        uint? sourceId = uint.TryParse(configuration.Rid, out uint parsedSourceId) &&
            parsedSourceId > 0 &&
            parsedSourceId <= 0xFFFFFF
            ? parsedSourceId
            : null;

        return new FneConnectionOptions(
            configuration.Name.Trim(),
            string.IsNullOrWhiteSpace(configuration.Identity) ? configuration.PeerId.ToString() : configuration.Identity.Trim(),
            configuration.Address.Trim(),
            configuration.Port,
            configuration.PeerId,
            configuration.Password,
            configuration.Encrypted,
            configuration.Encrypted ? configuration.PresharedKey : null)
        {
            SourceId = sourceId,
            KmfPresharedKey = configuration.KmfPresharedKey
        };
    }
}

public sealed record FneConnectionStatus(
    string Name,
    FneConnectionState State,
    string Message,
    DateTimeOffset ChangedAt);

/// <summary>
/// Sanitized P25 key response. Raw KMM frames and transport payloads are not
/// exposed to the desktop or media layers.
/// </summary>
public sealed record FneKeyResponse(
    string SystemName,
    byte AlgorithmId,
    ushort KeyId,
    ReadOnlyMemory<byte> KeyMaterial);

/// <summary>
/// Owns one cross-platform FNE peer lifecycle. It does not start until StartAsync is called.
/// </summary>
public sealed class FneConnection : IAsyncDisposable
{
    private readonly FneConnectionOptions options;
    private readonly object sync = new();
    private FnePeer? peer;
    private FneConnectionStatus status;
    private CancellationTokenSource? stateMonitorCancellation;
    private Task? stateMonitorTask;

    public FneConnection(FneConnectionOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        status = new FneConnectionStatus(options.Name, FneConnectionState.Disconnected, "Not started", DateTimeOffset.UtcNow);
    }

    public event EventHandler<FneConnectionStatus>? StatusChanged;
    public event EventHandler<FneTrafficFrame>? TrafficReceived;
    public event EventHandler<FneKeyResponse>? KeyResponseReceived;

    public FneConnectionStatus Status
    {
        get
        {
            lock (sync)
                return status;
        }
    }

    public FnePeer? Peer
    {
        get
        {
            lock (sync)
                return peer;
        }
    }

    public uint CreateStreamId()
    {
        uint streamId;
        do
        {
            streamId = FneBase.CreateStreamID();
        }
        while (streamId == 0);

        return streamId;
    }

    /// <summary>
    /// Sends one protocol payload through the active FNE traffic channel.
    /// Protocol-specific packet construction stays in the media layer while
    /// this service owns connection state and the legacy transport adapter.
    /// </summary>
    public void SendTraffic(
        FneTrafficProtocol protocol,
        ReadOnlySpan<byte> payload,
        ushort packetSequence,
        uint streamId)
    {
        FnePeer current;
        lock (sync)
        {
            current = peer ?? throw new InvalidOperationException("The FNE connection is not started.");
            if (status.State != FneConnectionState.Connected)
                throw new InvalidOperationException($"The FNE connection is not ready for traffic ({status.State}).");
        }

        var opcode = protocol switch
        {
            FneTrafficProtocol.Dmr => new Tuple<byte, byte>(Constants.NET_FUNC_PROTOCOL, Constants.NET_PROTOCOL_SUBFUNC_DMR),
            FneTrafficProtocol.P25 => new Tuple<byte, byte>(Constants.NET_FUNC_PROTOCOL, Constants.NET_PROTOCOL_SUBFUNC_P25),
            FneTrafficProtocol.Nxdn => new Tuple<byte, byte>(Constants.NET_FUNC_PROTOCOL, Constants.NET_PROTOCOL_SUBFUNC_NXDN),
            FneTrafficProtocol.Analog => new Tuple<byte, byte>(Constants.NET_FUNC_PROTOCOL, Constants.NET_PROTOCOL_SUBFUNC_ANALOG),
            _ => throw new ArgumentOutOfRangeException(nameof(protocol))
        };

        current.SendMasterTraffic(opcode, payload.ToArray(), packetSequence, streamId);
    }

    /// <summary>
    /// Requests one P25 key from the connected FNE. The response is accepted
    /// only through the sanitized key callback below.
    /// </summary>
    public void RequestP25Key(byte algorithmId, ushort keyId)
    {
        if (!IsSupportedP25Algorithm(algorithmId))
            throw new ArgumentOutOfRangeException(nameof(algorithmId), "Unsupported P25 encryption algorithm.");
        if (keyId == 0)
            throw new ArgumentOutOfRangeException(nameof(keyId), "P25 key ID must be non-zero.");

        FnePeer current;
        lock (sync)
        {
            current = peer ?? throw new InvalidOperationException("The FNE connection is not started.");
            if (status.State != FneConnectionState.Connected)
                throw new InvalidOperationException($"The FNE connection is not ready for key management ({status.State}).");
            if (options.SourceId is not uint sourceId)
                throw new InvalidOperationException("A source ID is required for P25 key management.");

            current.SendMasterKeyRequest(algorithmId, keyId, sourceId);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            if (peer is not null)
                throw new InvalidOperationException("The FNE connection is already started.");
        }

        Publish(FneConnectionState.Starting, $"Resolving {options.Address}:{options.Port}");
        FnePeer? candidate = null;

        try
        {
            IPEndPoint endpoint = await ResolveEndpointAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            candidate = CreatePeer(endpoint);
            lock (sync)
                peer = candidate;

            candidate.Start();
            StartStateMonitor(candidate);
            Publish(FneConnectionState.WaitingForLogin, "FNE network services started; waiting for login");
        }
        catch (Exception exception)
        {
            lock (sync)
            {
                if (ReferenceEquals(peer, candidate))
                    peer = null;
            }

            StopStateMonitor();

            Publish(FneConnectionState.Faulted, exception.Message);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        FnePeer? current;
        CancellationTokenSource? monitorCancellation;
        Task? monitorTask;
        lock (sync)
        {
            current = peer;
            peer = null;
            monitorCancellation = stateMonitorCancellation;
            monitorTask = stateMonitorTask;
            stateMonitorCancellation = null;
            stateMonitorTask = null;
        }

        if (current is null)
        {
            Publish(FneConnectionState.Disconnected, "Not started");
            return;
        }

        Publish(FneConnectionState.Stopping, "Stopping FNE network services");
        monitorCancellation?.Cancel();
        if (monitorTask is not null)
        {
            try
            {
                await monitorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }
        monitorCancellation?.Dispose();
        current.PeerConnected -= HandlePeerConnected;
        current.KeyResponse -= HandleKeyResponse;
        current.PeerDisconnected = null;
        DetachTrafficHandlers(current);

        try
        {
            await Task.Run(current.Stop, cancellationToken).ConfigureAwait(false);
            Publish(FneConnectionState.Disconnected, "Stopped");
        }
        catch (ObjectDisposedException)
        {
            Publish(FneConnectionState.Disconnected, "Stopped");
        }
        catch (SocketException exception)
        {
            Publish(FneConnectionState.Disconnected, $"Stopped; close packet was not sent: {exception.SocketErrorCode}");
        }
        catch (InvalidOperationException)
        {
            Publish(FneConnectionState.Disconnected, "Stopped");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private FnePeer CreatePeer(IPEndPoint endpoint)
    {
        var created = new FnePeer("DVMCONSOLE", options.PeerId, endpoint, options.PresharedKey);
        created.Passphrase = options.Password;
        created.PingTime = 5;
        created.LogLevel = options.EnableDiagnostics ? LogLevel.DEBUG : LogLevel.INFO;
        created.RawPacketTrace = options.EnableDiagnostics;
        created.Information = new PeerInformation
        {
            PeerID = options.PeerId,
            State = ConnectionState.WAITING_LOGIN,
            Details = new PeerDetails
            {
                ConventionalPeer = true,
                PeerClass = PeerConnectionClass.PEER_CONN_CLASS_CONSOLE,
                Software = "CONSOLE_REBUILD",
                Identity = options.Identity
            }
        };
        created.Logger = HandlePeerLog;
        if (!string.IsNullOrWhiteSpace(options.KmfPresharedKey))
            created.SetKMFPresharedKey(options.KmfPresharedKey);
        created.PeerConnected += HandlePeerConnected;
        created.KeyResponse += HandleKeyResponse;
        created.PeerDisconnected = _ => Publish(FneConnectionState.WaitingForLogin, "FNE peer disconnected; waiting to reconnect");
        created.DMRDataReceived += HandleDmrDataReceived;
        created.P25DataReceived += HandleP25DataReceived;
        created.NXDNDataReceived += HandleNxdnDataReceived;
        created.AnalogDataReceived += HandleAnalogDataReceived;
        return created;
    }

    private void HandlePeerConnected(object? sender, PeerConnectedEvent args)
    {
        Publish(FneConnectionState.Connected, "FNE peer connected");
    }

    private void HandleKeyResponse(object? sender, KeyResponseEvent args)
    {
        if (args.MessageId != (byte)KmmMessageType.MODIFY_KEY_CMD || args.KmmKey is null)
            return;

        bool peerEncrypted = args.KmmKey.DecryptInfoFmt == P25Defines.KMM_DECRYPT_PEER_ENC;
        if (args.KmmKey.DecryptInfoFmt is not (P25Defines.KMM_DECRYPT_INSTRUCTION_NONE or P25Defines.KMM_DECRYPT_INSTRUCTION_MI) &&
            (!peerEncrypted || string.IsNullOrWhiteSpace(options.KmfPresharedKey)))
        {
            // Peer-encrypted KMM requires an explicitly configured KMF secret;
            // do not treat the FNE transport preshared key as that secret.
            return;
        }

        byte algorithmId = args.KmmKey.KeysetItem?.AlgId ?? 0;
        if (algorithmId == 0)
            algorithmId = args.KmmKey.AlgId;
        if (!IsSupportedP25Algorithm(algorithmId))
            return;

        foreach (KeyItem key in args.KmmKey.KeysetItem?.Keys ?? [])
        {
            if (key.KeyId == 0)
                continue;

            byte[] material = key.GetKey();
            if (material.Length == 0)
                continue;

            KeyResponseReceived?.Invoke(this, new FneKeyResponse(
                options.Name,
                algorithmId,
                key.KeyId,
                material));
        }
    }

    private void HandleDmrDataReceived(object? sender, DMRDataReceivedEvent args)
    {
        PublishTraffic(new FneTrafficFrame(
            FneTrafficProtocol.Dmr,
            args.PeerId,
            args.SrcId,
            args.DstId,
            args.Slot,
            args.CallType.ToString(),
            args.FrameType.ToString(),
            args.DataType.ToString(),
            args.PacketSequence,
            args.StreamId,
            args.Data));
    }

    private void HandleP25DataReceived(object? sender, P25DataReceivedEvent args)
    {
        PublishTraffic(new FneTrafficFrame(
            FneTrafficProtocol.P25,
            args.PeerId,
            args.SrcId,
            args.DstId,
            null,
            args.CallType.ToString(),
            args.FrameType.ToString(),
            args.DUID.ToString(),
            args.PacketSequence,
            args.StreamId,
            args.Data));
    }

    private void HandleNxdnDataReceived(object? sender, NXDNDataReceivedEvent args)
    {
        PublishTraffic(new FneTrafficFrame(
            FneTrafficProtocol.Nxdn,
            args.PeerId,
            args.SrcId,
            args.DstId,
            null,
            args.CallType.ToString(),
            args.FrameType.ToString(),
            args.MessageType.ToString(),
            args.PacketSequence,
            args.StreamId,
            args.Data));
    }

    private void HandleAnalogDataReceived(object? sender, AnalogDataReceivedEvent args)
    {
        PublishTraffic(new FneTrafficFrame(
            FneTrafficProtocol.Analog,
            args.PeerId,
            args.SrcId,
            args.DstId,
            null,
            args.CallType.ToString(),
            args.FrameType.ToString(),
            args.AudioFrameType.ToString(),
            args.PacketSequence,
            args.StreamId,
            args.Data));
    }

    private void PublishTraffic(FneTrafficFrame frame)
    {
        TrafficReceived?.Invoke(this, frame);
    }

    private void DetachTrafficHandlers(FnePeer current)
    {
        current.KeyResponse -= HandleKeyResponse;
        current.DMRDataReceived -= HandleDmrDataReceived;
        current.P25DataReceived -= HandleP25DataReceived;
        current.NXDNDataReceived -= HandleNxdnDataReceived;
        current.AnalogDataReceived -= HandleAnalogDataReceived;
    }

    private static bool IsSupportedP25Algorithm(byte algorithmId)
    {
        return algorithmId is P25Defines.P25_ALGO_AES or
            P25Defines.P25_ALGO_DES or
            P25Defines.P25_ALGO_ARC4;
    }

    private void HandlePeerLog(LogLevel level, string message)
    {
        if (message.Contains("Sending login request", StringComparison.OrdinalIgnoreCase))
        {
            Publish(FneConnectionState.WaitingForLogin, "FNE login request sent");
            return;
        }

        if (message.Contains("Network Sent", StringComparison.OrdinalIgnoreCase))
        {
            Publish(Status.State, "FNE traffic packet sent");
            return;
        }

        if (message.Contains("Network Received", StringComparison.OrdinalIgnoreCase))
        {
            Publish(Status.State, "FNE traffic packet received");
            return;
        }

        if (message.Contains("login ACK received", StringComparison.OrdinalIgnoreCase))
        {
            Publish(FneConnectionState.Authenticating, "FNE login acknowledgement received");
            return;
        }

        if (message.Contains("master NAK", StringComparison.OrdinalIgnoreCase))
        {
            Publish(FneConnectionState.Faulted, "FNE master rejected the connection");
            return;
        }

        if (message.Contains("SOCKET ERROR", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Not connected or lost connection", StringComparison.OrdinalIgnoreCase))
        {
            Publish(FneConnectionState.Faulted, "FNE socket error or connection loss");
            return;
        }

        if (level is LogLevel.ERROR or LogLevel.FATAL)
            Publish(FneConnectionState.Faulted, "FNE protocol error");
    }

    private void StartStateMonitor(FnePeer current)
    {
        var cancellation = new CancellationTokenSource();
        lock (sync)
        {
            stateMonitorCancellation = cancellation;
            stateMonitorTask = MonitorPeerStateAsync(current, cancellation.Token);
        }
    }

    private void StopStateMonitor()
    {
        CancellationTokenSource? cancellation;
        lock (sync)
        {
            cancellation = stateMonitorCancellation;
            stateMonitorCancellation = null;
            stateMonitorTask = null;
        }

        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private async Task MonitorPeerStateAsync(FnePeer current, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        FneConnectionState? lastState = null;

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                FneConnectionState nextState = current.Information?.State switch
                {
                    ConnectionState.WAITING_AUTHORISATION => FneConnectionState.Authenticating,
                    ConnectionState.WAITING_CONFIG => FneConnectionState.Configuring,
                    ConnectionState.RUNNING => FneConnectionState.Connected,
                    _ => FneConnectionState.WaitingForLogin
                };

                if (nextState == lastState)
                    continue;

                lastState = nextState;
                Publish(nextState, nextState switch
                {
                    FneConnectionState.Authenticating => "FNE login accepted; waiting for authorization",
                    FneConnectionState.Configuring => "FNE authorization accepted; sending configuration",
                    FneConnectionState.Connected => "FNE peer connected",
                    _ => "Waiting for FNE login acknowledgement"
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
    }

    private async Task<IPEndPoint> ResolveEndpointAsync(CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(options.Address, out IPAddress? address))
            return new IPEndPoint(address, options.Port);

        IPAddress[] addresses = await Dns.GetHostAddressesAsync(options.Address, cancellationToken).ConfigureAwait(false);
        IPAddress? resolved = addresses.FirstOrDefault(candidate => candidate.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault();
        return resolved is null
            ? throw new InvalidOperationException($"Could not resolve FNE address '{options.Address}'.")
            : new IPEndPoint(resolved, options.Port);
    }

    private void Publish(FneConnectionState state, string message)
    {
        FneConnectionStatus next = new(options.Name, state, message, DateTimeOffset.UtcNow);
        lock (sync)
            status = next;
        StatusChanged?.Invoke(this, next);
    }
}
