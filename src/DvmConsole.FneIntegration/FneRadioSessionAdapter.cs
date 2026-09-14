// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Diagnostics;
using DvmConsole.Core.Runtime;
using DvmConsole.FneClient;

namespace DvmConsole.FneIntegration;

/// <summary>
/// Host-independent FNE adapter. It owns the concrete FNE connection while exposing
/// stable-ID, protocol-neutral radio session events to Application.
/// </summary>
public sealed class FneRadioSessionAdapter : IFneRadioSession, IFneTrafficEndpoint, IRadioConnectionStateNotifications, IRadioLogSource, IRadioSubscriberCommandEndpoint, IRadioP25KeyEndpoint, IRadioSubscriberAcknowledgementSource
{
    private readonly FneConnection connection;
    private readonly FneConnectionOptions options;
    private readonly TransmitChannelDescriptor[] channels;
    private readonly ChannelId[] channelIds;
    private readonly IReadOnlyDictionary<(ChannelProtocol Protocol, uint DestinationId), ChannelId[]>
        receiveRouteIndex;
    private readonly IClock clock;

    public FneRadioSessionAdapter(
        FneConnectionOptions options,
        Func<IReadOnlyCollection<TransmitChannelDescriptor>> getChannels,
        IClock? clock = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(getChannels);
        channels = getChannels().ToArray();
        channelIds = channels.Select(static channel => channel.Id).ToArray();
        receiveRouteIndex = channels
            .GroupBy(static channel => (
                channel.Definition.Protocol,
                channel.Definition.DestinationId))
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(channel => channel.Id).ToArray());
        this.clock = clock ?? SystemClock.Instance;
        connection = new FneConnection(options);
        SystemId = SystemId.FromName(options.Name);

        connection.StatusChanged += HandleConnectionStatus;
        connection.LogReceived += HandleLogReceived;
        connection.TrafficReceived += HandleTrafficReceived;
        connection.KeyResponseReceived += HandleKeyResponse;
        connection.SubscriberResponseReceived += HandleSubscriberResponse;
        connection.TalkgroupAuthorityChanged += HandleTalkgroupAuthorityChanged;
    }

    public SystemId SystemId { get; }
    public string Name => options.Name;
    public IReadOnlyCollection<TransmitChannelDescriptor> ChannelDescriptors => channels;
    public IReadOnlyCollection<ChannelId> ChannelIds => channelIds;
    public bool IsConnected => connection.Status.State == FneConnectionState.Connected;
    public bool IsConnectionActive => connection.Status.State is not (
        FneConnectionState.Disconnected or FneConnectionState.Faulted);
    public uint? SourceId => options.SourceId;
    public string Identity => options.Identity;
    public FneConnectionStatus Status => connection.Status;
    public RadioConnectionSnapshot ConnectionState
    {
        get
        {
            FneConnectionStatus current = connection.Status;
            return new(SystemId, current.Name, FneConnectionStateMapper.ToApplicationState(current.State),
                current.Message, current.ChangedAt);
        }
    }

    public event EventHandler<RadioTrafficRecord>? TrafficReceived;
    public event EventHandler<TalkgroupAuthorityRecord>? AuthorityChanged;
    public event EventHandler<FneConnectionStatus>? StatusChanged;
    public event EventHandler? ConnectionStateChanged;
    public event EventHandler<FneLogEntry>? LogReceived;
    public event EventHandler<DebugLogEntry>? LogPublished;
    public event EventHandler<FneKeyResponse>? KeyResponseReceived;
    public event EventHandler<RadioP25KeyResponse>? P25KeyReceived;
    public event EventHandler<ConsoleSubscriberAcknowledgement>? SubscriberAcknowledged;

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
        => new(connection.StartOrReconnectAsync(cancellationToken));

    public ValueTask QuiesceAsync(CancellationToken cancellationToken = default)
        => new(connection.StopAsync(cancellationToken));

    public Task StopAsync(CancellationToken cancellationToken = default)
        => connection.StopAsync(cancellationToken);

    public void Abort() => connection.Abort();

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
        ReadOnlyMemory<byte> payload,
        ushort packetSequence,
        uint streamId)
        => connection.SendTraffic(protocol, payload, packetSequence, streamId);

    public void SendTraffic(
        RadioMediaProtocol protocol,
        ReadOnlyMemory<byte> payload,
        ushort packetSequence,
        uint streamId)
        => SendTraffic(ToFneProtocol(protocol), payload, packetSequence, streamId);

    public void RequestP25Key(byte algorithmId, ushort keyId)
        => connection.RequestP25Key(algorithmId, keyId);

    public void SendP25SubscriberCommand(P25SubscriberCommand command, uint destinationId)
        => connection.SendP25SubscriberCommand(command, destinationId);

    public void SendSubscriberCommand(ConsoleSubscriberCommand command, uint destinationId)
        => SendP25SubscriberCommand(FneSubscriberCommandBindings.ToFne(command), destinationId);

    public async ValueTask DisposeAsync()
    {
        connection.StatusChanged -= HandleConnectionStatus;
        connection.LogReceived -= HandleLogReceived;
        connection.TrafficReceived -= HandleTrafficReceived;
        connection.KeyResponseReceived -= HandleKeyResponse;
        connection.SubscriberResponseReceived -= HandleSubscriberResponse;
        connection.TalkgroupAuthorityChanged -= HandleTalkgroupAuthorityChanged;
        await connection.DisposeAsync().ConfigureAwait(false);
    }

    private void HandleConnectionStatus(object? sender, FneConnectionStatus status)
    {
        foreach (EventHandler observer in ConnectionStateChanged?.GetInvocationList() ?? [])
        {
            try { observer(this, EventArgs.Empty); }
            catch { /* A detached observer cannot interrupt connection recovery. */ }
        }
        StatusChanged?.Invoke(this, status);
    }

    private void HandleLogReceived(object? sender, FneLogEntry entry)
    {
        LogReceived?.Invoke(this, entry);
        var portable = new DebugLogEntry(entry.Timestamp, entry.SystemName, entry.Severity, entry.Message);
        foreach (EventHandler<DebugLogEntry> observer in LogPublished?.GetInvocationList() ?? [])
        {
            try { observer(this, portable); }
            catch { /* Diagnostic consumers cannot interrupt transport processing. */ }
        }
    }

    private void HandleTrafficReceived(object? sender, FneTrafficFrame traffic)
    {
        receiveRouteIndex.TryGetValue(
            (FneTrafficProtocolMapper.ToChannelProtocol(traffic.Protocol), traffic.DestinationId),
            out ChannelId[]? candidates);
        TrafficReceived?.Invoke(this, new RadioTrafficRecord(
            SystemId,
            candidates ?? [],
            traffic,
            clock.UtcNow,
            traffic.FneBoundaryTimestamp,
            traffic.TransportIngressTimestamp));
    }

    private void HandleSubscriberResponse(object? sender, P25SubscriberResponse response)
    {
        bool fneTarget = response.Command is P25SubscriberCommand.Inhibit or P25SubscriberCommand.Uninhibit &&
            response.DestinationId == fnecore.P25.P25Defines.WUID_FNE;
        if (!IsConnected || (!fneTarget && response.DestinationId != SourceId)) return;
        var acknowledgement = new ConsoleSubscriberAcknowledgement(SystemId,
            FneSubscriberCommandBindings.ToApplication(response.Command), response.SourceId);
        foreach (EventHandler<ConsoleSubscriberAcknowledgement> observer in SubscriberAcknowledged?.GetInvocationList() ?? [])
        {
            try { observer(this, acknowledgement); }
            catch { /* A detached observer cannot suppress another session consumer. */ }
        }
    }

    private void HandleKeyResponse(object? sender, FneKeyResponse response)
    {
        P25KeyReceived?.Invoke(this, new(SystemId, response.AlgorithmId, response.KeyId, response.KeyMaterial));
        KeyResponseReceived?.Invoke(this, response);
    }

    private void HandleTalkgroupAuthorityChanged(
        object? sender,
        FneTalkgroupAuthority authority)
    {
        DateTimeOffset observedAt = clock.UtcNow;
        TalkgroupAuthorityChannelRecord[] channelStates = channels
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
            channelStates,
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
