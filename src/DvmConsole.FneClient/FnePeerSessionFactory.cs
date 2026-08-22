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
    FnePeer Create(
        FneConnectionOptions options,
        IPEndPoint endpoint,
        string softwareIdentifier,
        FnePeerSessionCallbacks callbacks);
}

internal sealed class FnePeerSessionFactory : IFnePeerSessionFactory
{
    public FnePeer Create(
        FneConnectionOptions options,
        IPEndPoint endpoint,
        string softwareIdentifier,
        FnePeerSessionCallbacks callbacks)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(callbacks);

        using IDisposable encryptionScope = FneTransportEncryptionContext.Use(
            ToTransportMode(options.TransportEncryptionMode),
            callbacks.TrafficIngressObserved);
        var peer = new FnePeer("DVMCONSOLE", options.PeerId, endpoint, options.PresharedKey)
        {
            Passphrase = options.Password,
            PingTime = 5,
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
        return peer;
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
