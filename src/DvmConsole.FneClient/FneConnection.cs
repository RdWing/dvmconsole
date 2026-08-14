using System.Net;
using System.Net.Sockets;
using DvmConsole.Core.Configuration;
using fnecore;

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

        return new FneConnectionOptions(
            configuration.Name.Trim(),
            string.IsNullOrWhiteSpace(configuration.Identity) ? configuration.PeerId.ToString() : configuration.Identity.Trim(),
            configuration.Address.Trim(),
            configuration.Port,
            configuration.PeerId,
            configuration.Password,
            configuration.Encrypted,
            configuration.Encrypted ? configuration.PresharedKey : null);
    }
}

public sealed record FneConnectionStatus(
    string Name,
    FneConnectionState State,
    string Message,
    DateTimeOffset ChangedAt);

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
        current.PeerDisconnected = null;

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
        created.PeerConnected += HandlePeerConnected;
        created.PeerDisconnected = _ => Publish(FneConnectionState.WaitingForLogin, "FNE peer disconnected; waiting to reconnect");
        return created;
    }

    private void HandlePeerConnected(object? sender, PeerConnectedEvent args)
    {
        Publish(FneConnectionState.Connected, "FNE peer connected");
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
