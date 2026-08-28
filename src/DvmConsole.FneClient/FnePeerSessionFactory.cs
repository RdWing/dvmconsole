using System.Net;
using fnecore;

namespace DvmConsole.FneClient;

internal sealed record FnePeerSessionCallbacks(
    Action<LogLevel, string> Log,
    EventHandler<PeerConnectedEvent> Connected,
    EventHandler<KeyResponseEvent> KeyResponse,
    Action<uint> Disconnected,
    EventHandler<DMRDataReceivedEvent> DmrDataReceived,
    EventHandler<P25DataReceivedEvent> P25DataReceived,
    EventHandler<NXDNDataReceivedEvent> NxdnDataReceived,
    EventHandler<AnalogDataReceivedEvent> AnalogDataReceived,
    Action<long> TrafficIngressObserved);

internal interface IFnePeerSessionFactory
{
    IFnePeerSession Create(
        FneConnectionOptions options,
        IPEndPoint endpoint,
        string softwareIdentifier,
        FnePeerSessionCallbacks callbacks);
}

internal interface IFnePeerSession : IDisposable
{
    FnePeer Peer { get; }
    void Start();
    void Stop();
}

internal sealed class FnePeerSession : IFnePeerSession
{
    private readonly FneTransportLifetime transportLifetime;
    private int stopped;

    public FnePeerSession(FnePeer peer, FneTransportLifetime transportLifetime)
    {
        Peer = peer ?? throw new ArgumentNullException(nameof(peer));
        this.transportLifetime = transportLifetime ?? throw new ArgumentNullException(nameof(transportLifetime));
    }

    public FnePeer Peer { get; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref stopped) != 0, this);
        Peer.Start();
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0)
            return;

        transportLifetime.BeginStop();
        try
        {
            if (Peer.IsStarted)
                Peer.Stop();
        }
        finally
        {
            transportLifetime.Dispose();
        }
    }

    public void Dispose() => Stop();
}

internal sealed class FnePeerSessionFactory : IFnePeerSessionFactory
{
    internal const int DefaultPingIntervalSeconds = 5;

    public IFnePeerSession Create(
        FneConnectionOptions options,
        IPEndPoint endpoint,
        string softwareIdentifier,
        FnePeerSessionCallbacks callbacks)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(callbacks);
        ValidateSessionPrerequisites(options);

        var transportLifetime = new FneTransportLifetime();
        try
        {
            using IDisposable encryptionScope = FneTransportEncryptionContext.Use(
                ToTransportMode(options.TransportEncryptionMode),
                callbacks.TrafficIngressObserved,
                transportLifetime);
            var peer = new FnePeer("DVMCONSOLE", options.PeerId, endpoint, options.PresharedKey)
            {
                Passphrase = options.Password,
                PingTime = DefaultPingIntervalSeconds,
                // The operator debug viewer is the console's complete FNE log sink.
                // Raw packet tracing remains separately opt-in so payload dumps are
                // not exposed by enabling the ordinary protocol log stream.
                LogLevel = LogLevel.DEBUG,
                RawPacketTrace = options.EnableDiagnostics
            };

            // Preserve the constructor-owned PeerInformation instance. Newer
            // fnecore revisions retain connection state on this object while the
            // RPTC payload reads the configured identity from its Details member.
            peer.Information.PeerID = options.PeerId;
            peer.Information.State = ConnectionState.WAITING_LOGIN;
            peer.Information.Details = new PeerDetails
            {
                ConventionalPeer = true,
                PeerClass = PeerConnectionClass.PEER_CONN_CLASS_CONSOLE,
                Software = softwareIdentifier,
                Identity = options.Identity
            };
            peer.Logger = callbacks.Log;
            if (!string.IsNullOrWhiteSpace(options.KmfPresharedKey))
                peer.SetKMFPresharedKey(options.KmfPresharedKey);
            peer.PeerConnected += callbacks.Connected;
            peer.KeyResponse += callbacks.KeyResponse;
            peer.PeerDisconnected = callbacks.Disconnected;
            peer.DMRDataReceived += callbacks.DmrDataReceived;
            peer.P25DataReceived += callbacks.P25DataReceived;
            peer.NXDNDataReceived += callbacks.NxdnDataReceived;
            peer.AnalogDataReceived += callbacks.AnalogDataReceived;
            return new FnePeerSession(peer, transportLifetime);
        }
        catch
        {
            transportLifetime.Dispose();
            throw;
        }
    }

    internal static void ValidateSessionPrerequisites(FneConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Password))
        {
            throw new InvalidOperationException(
                $"FNE system '{options.Name}' requires a password before it can connect.");
        }
        if (options.Encrypted && string.IsNullOrWhiteSpace(options.PresharedKey))
        {
            throw new InvalidOperationException(
                $"FNE system '{options.Name}' requires a preshared key because transport encryption is enabled.");
        }
    }

    internal static FneTransportEncryptionMode ToTransportMode(
        FneTransportEncryptionPreference preference)
        => preference switch
        {
            FneTransportEncryptionPreference.Ecb => FneTransportEncryptionMode.Ecb,
            FneTransportEncryptionPreference.Cbc => FneTransportEncryptionMode.Cbc,
            _ => FneTransportEncryptionMode.Auto
        };
}
