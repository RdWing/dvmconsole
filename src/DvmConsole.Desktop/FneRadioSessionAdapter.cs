using DvmConsole.Application;
using DvmConsole.Core.Diagnostics;
using DvmConsole.Core.Runtime;
using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

/// <summary>
/// Desktop FNE adapter. It owns the concrete FNE connection while exposing
/// stable-ID, protocol-neutral radio session events to Application.
/// </summary>
internal sealed class FneRadioSessionAdapter : IFneRadioSession, IFneTrafficEndpoint
{
    private readonly FneConnection connection;
    private readonly FneConnectionOptions options;
    private readonly Func<IReadOnlyCollection<TransmitChannelDescriptor>> getChannels;
    private readonly IClock clock;

    public FneRadioSessionAdapter(
        FneConnectionOptions options,
        Func<IReadOnlyCollection<TransmitChannelDescriptor>> getChannels,
        IClock? clock = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.getChannels = getChannels ?? throw new ArgumentNullException(nameof(getChannels));
        this.clock = clock ?? SystemClock.Instance;
        connection = new FneConnection(options);
        SystemId = SystemId.FromName(options.Name);

        connection.StatusChanged += HandleConnectionStatus;
        connection.LogReceived += HandleLogReceived;
        connection.TrafficReceived += HandleTrafficReceived;
        connection.KeyResponseReceived += HandleKeyResponse;
        connection.TalkgroupAuthorityChanged += HandleTalkgroupAuthorityChanged;
    }

    public SystemId SystemId { get; }
    public string Name => options.Name;
    public IReadOnlyCollection<TransmitChannelDescriptor> ChannelDescriptors => getChannels();
    public IReadOnlyCollection<ChannelId> ChannelIds => ChannelDescriptors.Select(channel => channel.Id).ToArray();
    public bool IsConnected => connection.Status.State == FneConnectionState.Connected;
    public bool IsConnectionActive => connection.Status.State is not (
        FneConnectionState.Disconnected or FneConnectionState.Faulted);
    public uint? SourceId => options.SourceId;
    public string Identity => options.Identity;
    public FneConnectionStatus Status => connection.Status;

    public event EventHandler<RadioTrafficRecord>? TrafficReceived;
    public event EventHandler<TalkgroupAuthorityRecord>? AuthorityChanged;
    public event EventHandler<FneConnectionStatus>? StatusChanged;
    public event EventHandler<FneLogEntry>? LogReceived;
    public event EventHandler<FneTrafficFrame>? FneTrafficReceived;
    public event EventHandler<FneKeyResponse>? KeyResponseReceived;
    public event EventHandler<FneTalkgroupAuthority>? FneTalkgroupAuthorityChanged;

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
        => new(connection.StartOrReconnectAsync(cancellationToken));

    public ValueTask QuiesceAsync(CancellationToken cancellationToken = default)
        => new(connection.StopAsync(cancellationToken));

    public Task StopAsync(CancellationToken cancellationToken = default)
        => connection.StopAsync(cancellationToken);

    public void SetVerboseLogging(bool enabled)
        => connection.SetVerboseLogging(enabled);

    public uint CreateStreamId() => connection.CreateStreamId();

    public FneTalkgroupAvailability GetTalkgroupAvailability(
        FneTrafficProtocol protocol,
        uint destinationId,
        byte runtimeSlot)
        => connection.TalkgroupAuthority.GetAvailability(protocol, destinationId, runtimeSlot);

    public TargetAuthorityState GetTargetAuthority(
        RadioMediaProtocol protocol,
        uint destinationId,
        byte runtimeSlot)
        => ToTargetAuthority(GetTalkgroupAvailability(
            ToFneProtocol(protocol),
            destinationId,
            runtimeSlot));

    public void SendTraffic(
        FneTrafficProtocol protocol,
        ReadOnlySpan<byte> payload,
        ushort packetSequence,
        uint streamId)
        => connection.SendTraffic(protocol, payload, packetSequence, streamId);

    public void SendTraffic(
        RadioMediaProtocol protocol,
        ReadOnlySpan<byte> payload,
        ushort packetSequence,
        uint streamId)
        => SendTraffic(ToFneProtocol(protocol), payload, packetSequence, streamId);

    public void RequestP25Key(byte algorithmId, ushort keyId)
        => connection.RequestP25Key(algorithmId, keyId);

    public void SendP25SubscriberCommand(P25SubscriberCommand command, uint destinationId)
        => connection.SendP25SubscriberCommand(command, destinationId);

    public async ValueTask DisposeAsync()
    {
        connection.StatusChanged -= HandleConnectionStatus;
        connection.LogReceived -= HandleLogReceived;
        connection.TrafficReceived -= HandleTrafficReceived;
        connection.KeyResponseReceived -= HandleKeyResponse;
        connection.TalkgroupAuthorityChanged -= HandleTalkgroupAuthorityChanged;
        await connection.DisposeAsync().ConfigureAwait(false);
    }

    private void HandleConnectionStatus(object? sender, FneConnectionStatus status)
        => StatusChanged?.Invoke(this, status);

    private void HandleLogReceived(object? sender, FneLogEntry entry)
        => LogReceived?.Invoke(this, entry);

    private void HandleTrafficReceived(object? sender, FneTrafficFrame traffic)
    {
        FneTrafficReceived?.Invoke(this, traffic);
        ChannelId[] candidates = ChannelDescriptors
            .Where(channel =>
                channel.Definition.Protocol == FneTrafficProtocolMapper.ToChannelProtocol(traffic.Protocol) &&
                channel.Definition.DestinationId == traffic.DestinationId)
            .Select(channel => channel.Id)
            .ToArray();
        TrafficReceived?.Invoke(this, new RadioTrafficRecord(
            SystemId,
            candidates,
            traffic,
            clock.UtcNow,
            traffic.FneBoundaryTimestamp,
            traffic.TransportIngressTimestamp));
    }

    private void HandleKeyResponse(object? sender, FneKeyResponse response)
        => KeyResponseReceived?.Invoke(this, response);

    private void HandleTalkgroupAuthorityChanged(
        object? sender,
        FneTalkgroupAuthority authority)
    {
        FneTalkgroupAuthorityChanged?.Invoke(this, authority);
        DateTimeOffset observedAt = clock.UtcNow;
        TalkgroupAuthorityChannelRecord[] channels = ChannelDescriptors
            .Select(channel =>
            {
                TargetAuthorityState state = ToTargetAuthority(authority.GetAvailability(
                    FneTrafficProtocolMapper.FromChannelProtocol(channel.Definition.Protocol),
                    channel.Definition.DestinationId,
                    channel.Definition.Slot));
                return new TalkgroupAuthorityChannelRecord(
                    channel.Id,
                    state,
                    state == TargetAuthorityState.Unavailable
                        ? channel.AuthorityUnavailableReason
                        : null);
            })
            .ToArray();
        AuthorityChanged?.Invoke(this, new TalkgroupAuthorityRecord(
            SystemId,
            channels,
            observedAt));
    }

    private static TargetAuthorityState ToTargetAuthority(FneTalkgroupAvailability availability)
        => availability switch
        {
            FneTalkgroupAvailability.Available => TargetAuthorityState.Available,
            FneTalkgroupAvailability.Unavailable => TargetAuthorityState.Unavailable,
            _ => TargetAuthorityState.Pending
        };

    private static FneTrafficProtocol ToFneProtocol(RadioMediaProtocol protocol)
        => protocol switch
        {
            RadioMediaProtocol.Dmr => FneTrafficProtocol.Dmr,
            RadioMediaProtocol.P25 => FneTrafficProtocol.P25,
            RadioMediaProtocol.Nxdn => FneTrafficProtocol.Nxdn,
            RadioMediaProtocol.Analog => FneTrafficProtocol.Analog,
            _ => throw new ArgumentOutOfRangeException(nameof(protocol))
        };
}
