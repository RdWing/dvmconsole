// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Runtime.CompilerServices;
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
    Action<FneTalkgroupAnnouncement> TalkgroupAnnouncementReceived,
    Action<long> TrafficIngressObserved,
    Action LoginRequestSent);

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
    void Abort() => Dispose();
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
                PinnedFnePeerLifetime.Stop(Peer);
        }
        finally
        {
            transportLifetime.Dispose();
        }
    }

    public void Abort()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0)
            return;

        transportLifetime.BeginStop();
        try
        {
            PinnedFnePeerLifetime.Abort(Peer);
        }
        finally
        {
            transportLifetime.Dispose();
        }
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Adapts the pinned upstream peer's async-void listener lifetime to NEO's
/// socket-owned cancellation. Cancelling the upstream token can throw an
/// unhandled OperationCanceledException after its Task wrapper has completed;
/// closing the compatible receivers and setting the loop flag lets every loop
/// leave normally instead.
/// </summary>
internal static class PinnedFnePeerLifetime
{
    public static void Abort(FnePeer peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        AbortListening(peer) = true;
        IsStarted(peer) = false;
    }

    public static void Stop(FnePeer peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        AbortListening(peer) = true;
        try
        {
            peer.SendMasterTraffic(
                FneBase.CreateOpcode(
                    Constants.NET_FUNC_RPT_CLOSING,
                    Constants.NET_SUBFUNC_NOP),
                new byte[1],
                1,
                FneBase.CreateStreamID(),
                forceZeroStream: true);
        }
        finally
        {
            IsStarted(peer) = false;
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "abortListening")]
    private static extern ref bool AbortListening(FnePeer peer);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "isStarted")]
    private static extern ref bool IsStarted(FneBase peer);
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
            using IDisposable transportSessionScope = FneTransportSessionContext.Use(
                ToTransportMode(options.TransportEncryptionMode),
                new FneTransportObservers(
                    callbacks.TrafficIngressObserved,
                    callbacks.TalkgroupAnnouncementReceived,
                    callbacks.LoginRequestSent),
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
