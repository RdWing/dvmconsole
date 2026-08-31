using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Reflection;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Diagnostics;
using fnecore;
using fnecore.EDAC;
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

public enum FneTransportEncryptionPreference
{
    Auto,
    Ecb,
    Cbc
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
    // Radio/source ID used for outbound voice traffic. It is optional so
    // connections without a transmit RID can still be used for receive-only
    // monitoring.
    public uint? SourceId { get; init; }

    // Optional KMF key used only to decrypt peer-encrypted P25 KMM responses.
    // It is never inferred from the FNE transport preshared key.
    public string? KmfPresharedKey { get; init; }

    // Enables sanitized diagnostic callbacks used by the bounded live probe.
    // Raw packet contents are never exposed by the rebuild client.
    public bool EnableDiagnostics { get; init; }

    // Initial state for routine high-frequency protocol traces. The desktop
    // can update it at runtime from its persisted operator setting.
    public bool EnableVerboseLogging { get; init; } = VerboseDiagnosticLogging.IsEnabled;

    public FneTransportEncryptionPreference TransportEncryptionMode { get; init; } =
        FneTransportEncryptionPreference.Auto;

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
            KmfPresharedKey = configuration.KmfPresharedKey,
            TransportEncryptionMode = ParseTransportEncryptionMode(configuration.TransportEncryptionMode)
        };
    }

    private static FneTransportEncryptionPreference ParseTransportEncryptionMode(string? mode)
        => mode?.Trim().ToLowerInvariant() switch
        {
            "ecb" => FneTransportEncryptionPreference.Ecb,
            "cbc" => FneTransportEncryptionPreference.Cbc,
            _ => FneTransportEncryptionPreference.Auto
        };
}

public sealed record FneConnectionStatus(
    string Name,
    FneConnectionState State,
    string Message,
    DateTimeOffset ChangedAt);

public sealed record FneLogEntry(
    string SystemName,
    DebugLogSeverity Severity,
    string Message,
    DateTimeOffset Timestamp);

// Sanitized P25 key response. Raw KMM frames and transport payloads are not
// exposed to the desktop or media layers.
public sealed record FneKeyResponse(
    string SystemName,
    byte AlgorithmId,
    ushort KeyId,
    ReadOnlyMemory<byte> KeyMaterial);

// Owns one cross-platform FNE peer lifecycle. It does not start until StartAsync is called.
public sealed class FneConnection : IAsyncDisposable
{
    internal static readonly TimeSpan DefaultHandshakeProgressTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan DefaultHandshakeReconnectGracePeriod = TimeSpan.FromSeconds(1);
    internal static TimeSpan P25KeyResponseWindow => PendingP25KeyRequestTracker.ResponseWindow;

    internal static string SoftwareIdentifier => FormatSoftwareIdentifier(
        typeof(FneConnection).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion);

    private readonly FneConnectionOptions options;
    private readonly IFneEndpointResolver endpointResolver;
    private readonly IFnePeerSessionFactory peerSessionFactory;
    private readonly PendingP25KeyRequestTracker pendingP25KeyRequests;
    private readonly ReconnectBackoff loginRetryBackoff = new();
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan handshakeProgressTimeout;
    private readonly TimeSpan handshakeReconnectGracePeriod;
    private readonly FnePeerStateMonitor stateMonitor = new();
    private readonly object sync = new();
    private readonly object sendSync = new();
    private readonly Dictionary<(uint DestinationId, byte Slot), FneTalkgroupRule> talkgroupRules = [];
    private readonly Queue<FneTalkgroupAuthority> talkgroupAuthorityNotifications = [];
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private IFnePeerSession? peerSession;
    private CancellationTokenSource? handshakeRecoveryCancellation;
    private FneConnectionStatus status;
    private long latestTrafficTransportTimestamp;
    private bool verboseLoggingEnabled;
    private bool hasAuthoritativeTalkgroupTable;
    private bool publishingTalkgroupAuthorityNotifications;
    private FneTalkgroupAuthority talkgroupAuthority = FneTalkgroupAuthority.Pending;
    private long talkgroupAuthoritySession;
    private EventHandler<FneTrafficFrame>[] trafficReceivedHandlers = [];

    public FneConnection(FneConnectionOptions options)
        : this(options, TimeProvider.System)
    {
    }

    internal FneConnection(FneConnectionOptions options, TimeProvider timeProvider)
        : this(options, timeProvider, new FneEndpointResolver())
    {
    }

    internal FneConnection(
        FneConnectionOptions options,
        TimeProvider timeProvider,
        IFneEndpointResolver endpointResolver)
        : this(options, timeProvider, endpointResolver, new FnePeerSessionFactory())
    {
    }

    internal FneConnection(
        FneConnectionOptions options,
        TimeProvider timeProvider,
        IFneEndpointResolver endpointResolver,
        IFnePeerSessionFactory peerSessionFactory,
        TimeSpan? handshakeProgressTimeout = null,
        TimeSpan? handshakeReconnectGracePeriod = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        pendingP25KeyRequests = new PendingP25KeyRequestTracker(timeProvider);
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.endpointResolver = endpointResolver ?? throw new ArgumentNullException(nameof(endpointResolver));
        this.peerSessionFactory = peerSessionFactory ?? throw new ArgumentNullException(nameof(peerSessionFactory));
        this.handshakeProgressTimeout = handshakeProgressTimeout ?? DefaultHandshakeProgressTimeout;
        if (this.handshakeProgressTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(handshakeProgressTimeout));
        this.handshakeReconnectGracePeriod = handshakeReconnectGracePeriod ?? DefaultHandshakeReconnectGracePeriod;
        if (this.handshakeReconnectGracePeriod < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(handshakeReconnectGracePeriod));
        status = new FneConnectionStatus(options.Name, FneConnectionState.Disconnected, "Not started", DateTimeOffset.UtcNow);
        verboseLoggingEnabled = options.EnableVerboseLogging;
    }

    public event EventHandler<FneConnectionStatus>? StatusChanged;
    public event EventHandler<FneLogEntry>? LogReceived;
    public event EventHandler<FneTrafficFrame>? TrafficReceived
    {
        add
        {
            if (value is not null)
                AddTrafficHandler(value);
        }
        remove
        {
            if (value is not null)
                RemoveTrafficHandler(value);
        }
    }
    public event EventHandler<FneKeyResponse>? KeyResponseReceived;
    public event EventHandler<FneTalkgroupAuthority>? TalkgroupAuthorityChanged;

    public void SetVerboseLogging(bool enabled)
        => Volatile.Write(ref verboseLoggingEnabled, enabled);

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
                return peerSession?.Peer;
        }
    }

    public FneTalkgroupAuthority TalkgroupAuthority
    {
        get
        {
            lock (sync)
                return talkgroupAuthority;
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

    // Sends one protocol payload through the active FNE traffic channel.
    // Protocol-specific packet construction stays in the media layer while
    // this service owns connection state and the legacy transport adapter.
    public void SendTraffic(
        FneTrafficProtocol protocol,
        ReadOnlySpan<byte> payload,
        ushort packetSequence,
        uint streamId)
    {
        if (payload.IsEmpty)
            throw new ArgumentException("FNE traffic payload cannot be empty.", nameof(payload));
        if (streamId == 0)
            throw new ArgumentOutOfRangeException(nameof(streamId), "FNE traffic stream ID must be non-zero.");

        FnePeer current;
        lock (sync)
        {
            current = peerSession?.Peer ?? throw new InvalidOperationException("The FNE connection is not started.");
            if (status.State != FneConnectionState.Connected)
                throw new InvalidOperationException($"The FNE connection is not ready for traffic ({status.State}).");
        }

        byte[] ownedPayload = payload.ToArray();
        lock (sendSync)
        {
            current.SendMasterTraffic(
                FneTrafficMapper.ToOpcode(protocol),
                ownedPayload,
                packetSequence,
                streamId);
        }
    }

    // Requests one P25 key from the connected FNE. The response is accepted
    // only through the sanitized key callback below.
    public void RequestP25Key(byte algorithmId, ushort keyId)
    {
        if (!IsSupportedP25Algorithm(algorithmId))
            throw new ArgumentOutOfRangeException(nameof(algorithmId), "Unsupported P25 encryption algorithm.");
        if (keyId == 0)
            throw new ArgumentOutOfRangeException(nameof(keyId), "P25 key ID must be non-zero.");

        FnePeer current;
        DateTimeOffset expiresAt;
        lock (sync)
        {
            current = peerSession?.Peer ?? throw new InvalidOperationException("The FNE connection is not started.");
            if (status.State != FneConnectionState.Connected)
                throw new InvalidOperationException($"The FNE connection is not ready for key management ({status.State}).");
            if (options.SourceId is not uint sourceId)
                throw new InvalidOperationException("A source ID is required for P25 key management.");

            expiresAt = RegisterPendingP25KeyRequestCore(algorithmId, keyId);
            try
            {
                lock (sendSync)
                    current.SendMasterKeyRequest(algorithmId, keyId, sourceId);
            }
            catch
            {
                pendingP25KeyRequests.TryCancel(algorithmId, keyId, expiresAt);
                throw;
            }
        }
    }

    public void SendP25SubscriberCommand(P25SubscriberCommand command, uint destinationId)
    {
        FnePeer current;
        uint sourceId;
        lock (sync)
        {
            current = peerSession?.Peer ?? throw new InvalidOperationException("The FNE connection is not started.");
            if (status.State != FneConnectionState.Connected)
                throw new InvalidOperationException($"The FNE connection is not ready for subscriber commands ({status.State}).");
            sourceId = options.SourceId ?? throw new InvalidOperationException("A source ID is required for P25 subscriber commands.");
        }

        P25SubscriberCommandMessage message = P25SubscriberCommandCodec.Build(command, sourceId, destinationId);
        var callData = new RemoteCallData
        {
            SrcId = sourceId,
            DstId = destinationId,
            LCO = message.LinkControlOpcode
        };

        // FnePeer intentionally exposes only raw traffic. Build the TSDU
        // framing at this client boundary instead of adding a console-specific
        // API to fnecore.
        byte[] payload = P25SubscriberFrameEncoder.Encode(message, callData);

        lock (sendSync)
        {
            current.SendMasterTraffic(
                FneBase.CreateOpcode(Constants.NET_FUNC_PROTOCOL, Constants.NET_PROTOCOL_SUBFUNC_P25),
                payload,
                current.pktSeq(true),
                callData.TxStreamID);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ResetLoginCadence();
            await StartCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycle.Release();
        }
    }

    // Starts the connection, replacing a retained peer after a fault or
    // transport loss. This is the operation used by the desktop Connect
    // command when the status is no longer Connected.
    public async Task StartOrReconnectAsync(CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Peer is not null)
                await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            ResetLoginCadence();
            await StartCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycle.Release();
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (peerSession is not null)
                throw new InvalidOperationException("The FNE connection is already started.");
        }
        ResetTalkgroupAuthority();

        Publish(FneConnectionState.Starting, $"Resolving {options.Address}:{options.Port}");
        IFnePeerSession? candidate = null;

        try
        {
            IPEndPoint endpoint = await endpointResolver
                .ResolveAsync(options.Address, options.Port, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            candidate = CreatePeerSession(endpoint);
            lock (sync)
                peerSession = candidate;

            ResetLoginCadence(candidate.Peer);
            candidate.Start();
            StartStateMonitor(candidate.Peer);
            Publish(FneConnectionState.WaitingForLogin, "FNE network services started; waiting for login");
        }
        catch (Exception exception)
        {
            lock (sync)
            {
                if (ReferenceEquals(peerSession, candidate))
                    peerSession = null;
            }
            InvalidateTalkgroupAuthoritySession();

            stateMonitor.Cancel();

            if (candidate is not null)
            {
                DetachPeerHandlers(candidate.Peer);
                try
                {
                    await Task.Run(candidate.Stop, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception cleanupException) when (cleanupException is
                    ObjectDisposedException or SocketException or InvalidOperationException)
                {
                    Debug.WriteLine($"FNE startup cleanup: {cleanupException.Message}");
                }
            }

            Publish(FneConnectionState.Faulted, exception.Message);
            ResetTalkgroupAuthority();
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycle.Release();
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        IFnePeerSession? current;
        lock (sync)
        {
            current = peerSession;
            peerSession = null;
            pendingP25KeyRequests.Clear();
        }
        InvalidateTalkgroupAuthoritySession();

        if (current is null)
        {
            ResetTalkgroupAuthority();
            Publish(FneConnectionState.Disconnected, "Not started");
            return;
        }

        Publish(FneConnectionState.Stopping, "Stopping FNE network services");
        ResetTalkgroupAuthority();
        await stateMonitor.StopAsync().ConfigureAwait(false);
        DetachPeerHandlers(current.Peer);

        try
        {
            // Once ownership has been removed from the connection, teardown
            // must finish even if the caller cancels its original operation.
            await Task.Run(current.Stop, CancellationToken.None).ConfigureAwait(false);
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

    internal IFnePeerSession CreatePeerSession(IPEndPoint endpoint)
    {
        long authoritySession = Interlocked.Increment(ref talkgroupAuthoritySession);
        return peerSessionFactory.Create(
            options,
            endpoint,
            SoftwareIdentifier,
            new FnePeerSessionCallbacks(
                HandlePeerLog,
                HandlePeerConnected,
                HandleKeyResponse,
                HandlePeerDisconnected,
                HandleDmrDataReceived,
                HandleP25DataReceived,
                HandleNxdnDataReceived,
                HandleAnalogDataReceived,
                announcement => HandleTalkgroupAnnouncement(authoritySession, announcement),
                timestamp => Volatile.Write(ref latestTrafficTransportTimestamp, timestamp),
                HandleLoginRequestSent));
    }

    internal static string FormatSoftwareIdentifier(string? informationalVersion)
    {
        const string fallbackVersion = "UNKNOWN";
        string version = string.IsNullOrWhiteSpace(informationalVersion)
            ? fallbackVersion
            : informationalVersion.Trim().Split('+', 2)[0];
        return $"DVMC_NEO_{version}";
    }

    private void HandlePeerConnected(object? sender, PeerConnectedEvent args)
    {
        if (sender is FnePeer connectedPeer)
        {
            ResetLoginCadence(connectedPeer);
            FnePeerKeepaliveStreamInitializer.TryInitialize(connectedPeer);
        }

        Publish(FneConnectionState.Connected, "FNE peer connected");
    }

    private void HandlePeerDisconnected(uint _)
    {
        pendingP25KeyRequests.Clear();
        ResetTalkgroupAuthority();
        Publish(FneConnectionState.WaitingForLogin, "FNE peer disconnected; waiting to reconnect");
    }

    private void HandleTalkgroupAnnouncement(
        long authoritySession,
        FneTalkgroupAnnouncement announcement)
    {
        if (authoritySession != Volatile.Read(ref talkgroupAuthoritySession))
            return;

        bool publishNotifications = false;
        lock (sync)
        {
            if (authoritySession != Volatile.Read(ref talkgroupAuthoritySession))
                return;

            if (announcement.ContainsActiveTalkgroups)
            {
                hasAuthoritativeTalkgroupTable = true;
            }

            foreach (FneTalkgroupAnnouncementEntry entry in announcement.Entries)
            {
                talkgroupRules[(entry.DestinationId, entry.Slot)] = new FneTalkgroupRule(
                    entry.DestinationId,
                    entry.Slot,
                    announcement.ContainsActiveTalkgroups,
                    entry.AffiliationRequired,
                    entry.NonPreferred);
            }

            if (hasAuthoritativeTalkgroupTable)
            {
                talkgroupAuthority = FneTalkgroupAuthority.FromRules(talkgroupRules.Values);
                publishNotifications = EnqueueTalkgroupAuthorityNotificationLocked(talkgroupAuthority);
            }
        }

        if (publishNotifications)
        {
            PublishTalkgroupAuthorityLog(announcement);
            PublishTalkgroupAuthorityNotifications();
        }
    }

    private void PublishTalkgroupAuthorityLog(FneTalkgroupAnnouncement announcement)
    {
        int active;
        int total;
        lock (sync)
        {
            active = talkgroupRules.Values.Count(rule => rule.IsActive);
            total = talkgroupRules.Count;
        }
        string update = announcement.ContainsActiveTalkgroups ? "active" : "inactive";
        Raise(LogReceived, new FneLogEntry(
            options.Name,
            DebugLogSeverity.Info,
            $"FNE talkgroup table ingested {announcement.Entries.Count} {update} " +
            $"entr{(announcement.Entries.Count == 1 ? "y" : "ies")}; {active} active of {total} known",
            DateTimeOffset.UtcNow));
    }

    private void ResetTalkgroupAuthority()
    {
        bool publishNotifications = false;
        lock (sync)
        {
            bool changed = hasAuthoritativeTalkgroupTable || talkgroupRules.Count > 0;
            hasAuthoritativeTalkgroupTable = false;
            talkgroupRules.Clear();
            talkgroupAuthority = FneTalkgroupAuthority.Pending;
            if (changed)
            {
                publishNotifications = EnqueueTalkgroupAuthorityNotificationLocked(
                    FneTalkgroupAuthority.Pending);
            }
        }

        if (publishNotifications)
            PublishTalkgroupAuthorityNotifications();
    }

    private bool EnqueueTalkgroupAuthorityNotificationLocked(FneTalkgroupAuthority authority)
    {
        talkgroupAuthorityNotifications.Enqueue(authority);
        if (publishingTalkgroupAuthorityNotifications)
            return false;

        publishingTalkgroupAuthorityNotifications = true;
        return true;
    }

    private void PublishTalkgroupAuthorityNotifications()
    {
        while (true)
        {
            FneTalkgroupAuthority authority;
            lock (sync)
            {
                if (!talkgroupAuthorityNotifications.TryDequeue(out authority!))
                {
                    publishingTalkgroupAuthorityNotifications = false;
                    return;
                }
            }

            Raise(TalkgroupAuthorityChanged, authority);
        }
    }

    private void InvalidateTalkgroupAuthoritySession()
        => Interlocked.Increment(ref talkgroupAuthoritySession);

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

            TryPublishRequestedP25KeyResponse(algorithmId, key.KeyId, material);
        }
    }

    internal void RegisterPendingP25KeyRequest(byte algorithmId, ushort keyId)
        => RegisterPendingP25KeyRequestCore(algorithmId, keyId);

    private DateTimeOffset RegisterPendingP25KeyRequestCore(byte algorithmId, ushort keyId)
        => pendingP25KeyRequests.Register(algorithmId, keyId);

    internal bool TryPublishRequestedP25KeyResponse(
        byte algorithmId,
        ushort keyId,
        ReadOnlyMemory<byte> material)
    {
        if (!IsSupportedP25Algorithm(algorithmId) ||
            keyId == 0 ||
            !HasSupportedP25KeyLength(algorithmId, material.Length))
            return false;

        if (!pendingP25KeyRequests.TryConsume(algorithmId, keyId))
            return false;

        Raise(KeyResponseReceived, new FneKeyResponse(options.Name, algorithmId, keyId, material));
        return true;
    }

    private void HandleDmrDataReceived(object? sender, DMRDataReceivedEvent args)
    {
        long boundaryTimestamp = Stopwatch.GetTimestamp();
        PublishTraffic(FneTrafficMapper.FromDmr(
            args,
            boundaryTimestamp,
            Interlocked.Exchange(ref latestTrafficTransportTimestamp, 0)));
    }

    private void HandleP25DataReceived(object? sender, P25DataReceivedEvent args)
    {
        long boundaryTimestamp = Stopwatch.GetTimestamp();
        PublishTraffic(FneTrafficMapper.FromP25(
            args,
            boundaryTimestamp,
            Interlocked.Exchange(ref latestTrafficTransportTimestamp, 0)));
    }

    private void HandleNxdnDataReceived(object? sender, NXDNDataReceivedEvent args)
    {
        long boundaryTimestamp = Stopwatch.GetTimestamp();
        PublishTraffic(FneTrafficMapper.FromNxdn(
            args,
            boundaryTimestamp,
            Interlocked.Exchange(ref latestTrafficTransportTimestamp, 0)));
    }

    private void HandleAnalogDataReceived(object? sender, AnalogDataReceivedEvent args)
    {
        long boundaryTimestamp = Stopwatch.GetTimestamp();
        PublishTraffic(FneTrafficMapper.FromAnalog(
            args,
            boundaryTimestamp,
            Interlocked.Exchange(ref latestTrafficTransportTimestamp, 0)));
    }

    internal void PublishTraffic(FneTrafficFrame frame)
    {
        Raise(Volatile.Read(ref trafficReceivedHandlers), frame);
    }

    private void AddTrafficHandler(EventHandler<FneTrafficFrame> handler)
    {
        while (true)
        {
            EventHandler<FneTrafficFrame>[] current = Volatile.Read(ref trafficReceivedHandlers);
            var updated = new EventHandler<FneTrafficFrame>[current.Length + 1];
            Array.Copy(current, updated, current.Length);
            updated[^1] = handler;
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref trafficReceivedHandlers, updated, current),
                    current))
            {
                return;
            }
        }
    }

    private void RemoveTrafficHandler(EventHandler<FneTrafficFrame> handler)
    {
        while (true)
        {
            EventHandler<FneTrafficFrame>[] current = Volatile.Read(ref trafficReceivedHandlers);
            int removeIndex = Array.LastIndexOf(current, handler);
            if (removeIndex < 0)
                return;

            var updated = new EventHandler<FneTrafficFrame>[current.Length - 1];
            if (removeIndex > 0)
                Array.Copy(current, 0, updated, 0, removeIndex);
            if (removeIndex < current.Length - 1)
            {
                Array.Copy(
                    current,
                    removeIndex + 1,
                    updated,
                    removeIndex,
                    current.Length - removeIndex - 1);
            }
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref trafficReceivedHandlers, updated, current),
                    current))
            {
                return;
            }
        }
    }

    private void DetachPeerHandlers(FnePeer current)
    {
        current.PeerConnected -= HandlePeerConnected;
        current.KeyResponse -= HandleKeyResponse;
        current.PeerDisconnected = null;
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

    private static bool HasSupportedP25KeyLength(byte algorithmId, int length)
        => algorithmId switch
        {
            P25Defines.P25_ALGO_AES => length is >= 1 and <= 32,
            P25Defines.P25_ALGO_DES => length == 8,
            P25Defines.P25_ALGO_ARC4 => length == 5,
            _ => false
        };

    private void HandlePeerLog(LogLevel level, string message)
    {
        if (level == LogLevel.DEBUG &&
            !Volatile.Read(ref verboseLoggingEnabled) &&
            FneLogInterpreter.IsRoutineHealthyKeepalive(message))
            return;

        string displayMessage = DebugLogRedactor.Redact(message);
        Raise(LogReceived, new FneLogEntry(
            options.Name,
            FneLogInterpreter.MapSeverity(level),
            displayMessage,
            DateTimeOffset.UtcNow));
        // Logs are diagnostics only. Connection and recovery state comes from
        // the peer's typed state and lifecycle callbacks, so upstream wording
        // changes cannot alter transport behavior.
    }

    private void ResetLoginCadence(FnePeer? current = null)
    {
        loginRetryBackoff.Reset();
        current ??= Peer;
        if (current is not null)
            current.PingTime = FnePeerSessionFactory.DefaultPingIntervalSeconds;
    }

    private void HandleLoginRequestSent()
    {
        FnePeer? current;
        lock (sync)
            current = peerSession?.Peer;
        if (current is null)
            return;

        TimeSpan retryDelay = loginRetryBackoff.NextDelay(
            TimeSpan.FromSeconds(FnePeerSessionFactory.DefaultPingIntervalSeconds));
        current.PingTime = checked((int)retryDelay.TotalSeconds);
    }

    private void StartStateMonitor(FnePeer current)
        => stateMonitor.Start(current, () => Status.State, Publish);

    internal static bool ShouldPublishMonitoredState(
        FneConnectionState nextState,
        FneConnectionState? lastState,
        FneConnectionState publishedState)
        => FnePeerStateMonitor.ShouldPublish(nextState, lastState, publishedState);

    private void Publish(FneConnectionState state, string message)
    {
        FneConnectionStatus next = new(options.Name, state, message, DateTimeOffset.UtcNow);
        FneConnectionState previousState;
        lock (sync)
        {
            previousState = status.State;
            status = next;
        }

        ObserveHandshakeProgress(previousState, state);
        Raise(StatusChanged, next);
    }

    private void ObserveHandshakeProgress(
        FneConnectionState previousState,
        FneConnectionState state)
    {
        bool isHandshakeProgress = state is
            FneConnectionState.Authenticating or
            FneConnectionState.Configuring;

        if (isHandshakeProgress)
        {
            if (state != previousState)
                ArmHandshakeRecovery(state);
            return;
        }

        CancelHandshakeRecovery();
    }

    private void ArmHandshakeRecovery(FneConnectionState expectedState)
    {
        IFnePeerSession? expectedSession;
        CancellationTokenSource nextCancellation = new();
        CancellationTokenSource? previousCancellation;

        lock (sync)
        {
            expectedSession = peerSession;
            if (expectedSession is null)
            {
                nextCancellation.Dispose();
                return;
            }

            previousCancellation = handshakeRecoveryCancellation;
            handshakeRecoveryCancellation = nextCancellation;
        }

        previousCancellation?.Cancel();
        previousCancellation?.Dispose();
        _ = RecoverStalledHandshakeAsync(
            expectedSession,
            expectedState,
            nextCancellation.Token);
    }

    private void CancelHandshakeRecovery()
    {
        CancellationTokenSource? current;
        lock (sync)
        {
            current = handshakeRecoveryCancellation;
            handshakeRecoveryCancellation = null;
        }

        current?.Cancel();
        current?.Dispose();
    }

    private async Task RecoverStalledHandshakeAsync(
        IFnePeerSession expectedSession,
        FneConnectionState expectedState,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(
                handshakeProgressTimeout,
                timeProvider,
                cancellationToken).ConfigureAwait(false);
            await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (sync)
                {
                    if (!ReferenceEquals(peerSession, expectedSession) ||
                        status.State != expectedState)
                    {
                        return;
                    }
                }

                string phase = GetHandshakePhaseName(expectedState);
                Raise(LogReceived, new FneLogEntry(
                    options.Name,
                    DebugLogSeverity.Warning,
                    $"FNE {phase} made no progress; closing this system's network session " +
                    $"and retrying after a {handshakeReconnectGracePeriod.TotalSeconds:0.#} second grace period",
                    DateTimeOffset.UtcNow));
                Publish(
                    FneConnectionState.Faulted,
                    $"FNE {phase} stalled; recycling network session");

                await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.Delay(
                    handshakeReconnectGracePeriod,
                    timeProvider,
                    CancellationToken.None).ConfigureAwait(false);
                await StartCoreAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                lifecycle.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A successful handshake, explicit stop, or newer handshake stage
            // superseded this recovery attempt.
        }
        catch (Exception exception)
        {
            // StartCoreAsync publishes the actionable connection fault. Keep the
            // detached watchdog task from surfacing an unobserved exception.
            Debug.WriteLine($"FNE handshake recovery failed: {exception.Message}");
        }
    }

    private static string GetHandshakePhaseName(FneConnectionState state)
        => state switch
        {
            FneConnectionState.Authenticating => "authentication",
            FneConnectionState.Configuring => "configuration",
            _ => "handshake"
        };

    private void Raise<T>(EventHandler<T>? handlers, T args)
    {
        if (handlers is null)
            return;

        foreach (EventHandler<T> handler in handlers.GetInvocationList().Cast<EventHandler<T>>())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"FNE event handler failed: {exception.Message}");
            }
        }
    }

    private void Raise<T>(EventHandler<T>[] handlers, T args)
    {
        foreach (EventHandler<T> handler in handlers)
        {
            try
            {
                handler(this, args);
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"FNE event handler failed: {exception.Message}");
            }
        }
    }
}
