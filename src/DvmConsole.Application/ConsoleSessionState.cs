// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Runtime;
using DvmConsole.Operations;
using DvmConsole.Media;

namespace DvmConsole.Application;

/// <summary>A channel's session-owned media state and operator state.</summary>
public sealed class ConsoleChannelState
{
    internal ChannelMeterState Meter { get; }

    private int authority;
    public TargetAuthorityState Authority => (TargetAuthorityState)Volatile.Read(ref authority);
    public event EventHandler? AuthorityChanged;

    public bool SetAuthority(TargetAuthorityState value)
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
        if (Interlocked.Exchange(ref authority, (int)value) == (int)value) return false;
        foreach (EventHandler observer in AuthorityChanged?.GetInvocationList() ?? [])
        {
            try { observer(this, EventArgs.Empty); }
            catch { /* A presentation observer cannot prevent runtime state publication. */ }
        }
        return true;
    }

    private ConsoleChannelState[] receivePeers = [];
    public bool HasLocalReceivePresentation => Operator.Snapshot is { AudioEnabled: true, AudioSuspended: false } &&
        (Runtime.State == ChannelRuntimeState.Receiving || Receive.Playback is not null);
    public ConsoleChannelState? ReceivePresentationOwner
    {
        get
        {
            if (Operator.Snapshot is not { AudioEnabled: true, AudioSuspended: false }) return null;
            if (HasLocalReceivePresentation) return this;
            return Volatile.Read(ref receivePeers).FirstOrDefault(peer => peer.HasLocalReceivePresentation);
        }
    }
    public uint? PresentedSourceId => Receive.Playback is { } playback ? playback.SourceId : Runtime.SourceId;
    public uint? PresentedStreamId => Receive.Playback?.StreamId ?? Runtime.StreamId;

    internal IReadOnlyList<ConsoleChannelState> ReceivePeers => Volatile.Read(ref receivePeers);

    internal static void LinkReceivePeers(IEnumerable<ConsoleChannelState> channels)
    {
        foreach (var group in channels.GroupBy(channel => channel.Identity.RouteKey))
        {
            ConsoleChannelState[] peers = group.ToArray();
            foreach (ConsoleChannelState channel in peers) Volatile.Write(ref channel.receivePeers, peers);
        }
    }

    public ConsoleChannelState(ChannelRuntimeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        SettingsKey = $"{definition.SystemName}\u001F{definition.Name}";
        Identity = ChannelDefinition.FromRuntime(definition, SettingsKey);
        Runtime = new ChannelRuntime(definition);
        Receive = new ChannelReceiveState(Runtime);
        Operator = new ChannelOperatorState(definition.IsEncrypted);
        Meter = new ChannelMeterState(Id);
    }

    public string SettingsKey { get; }

    public static ChannelId GetId(ChannelRuntimeDefinition definition)
        => new(ChannelDefinition.FromRuntime(definition,
            $"{definition.SystemName}\u001F{definition.Name}").SessionId);

    public TransmitChannelDescriptor CaptureTransmitDescriptor(ChannelConfigurationAccess access)
    {
        ArgumentNullException.ThrowIfNull(access);
        ChannelOperatorSnapshot operation = Operator.Snapshot;
        return new TransmitChannelDescriptor(Id, Runtime.Definition,
            ReceivePresentationOwner is not null, operation.TransmitEncrypted,
            access.CanTransmit(operation.TransmitEncrypted),
            access.TransmitUnavailableReason(operation.TransmitEncrypted),
            access.AuthorityUnavailableReason, operation.HasCallPriority);
    }

    internal bool SetTransmitEnabled(bool enabled, uint streamId = 0)
    {
        Operator.SetTransmitTransition(starting: false, stopping: false);
        if (enabled) Runtime.MarkTransmitting(streamId);
        else Runtime.MarkIdle();
        return Operator.SetTransmitEnabled(enabled);
    }

    internal bool SetReceiveEnabled(bool enabled)
    {
        if (!Operator.SetAudioEnabled(enabled)) return false;
        if (!enabled) Receive.ClearPlayback();
        return true;
    }

    internal bool SetAudioSuspended(bool suspended)
    {
        if (!Operator.SetAudioSuspended(suspended)) return false;
        if (suspended) Receive.ClearPlayback();
        return true;
    }

    public RecordingSubscriberPolicy RecordingSubscribers { get; } = new();
    public bool TryBeginReceivePlayback(uint sourceId, uint streamId)
    {
        if (streamId == 0 || Operator.Snapshot is not { AudioEnabled: true, AudioSuspended: false })
            return false;
        Receive.BeginMeter(streamId);
        return Receive.TryBeginPlayback(sourceId, streamId);
    }

    public void EndReceivePlayback(uint streamId)
    {
        Receive.EndMeter(streamId);
        Receive.EndPlayback(streamId);
    }

    public void MarkReceiveMeter(uint streamId, bool ended)
    {
        if (ended) Receive.EndMeter(streamId);
        else Receive.BeginMeter(streamId);
    }

    public ChannelRecordingDescriptor CaptureRecordingDescriptor(bool presentedReceive = false)
    {
        var operation = Operator.Snapshot;
        return new(Id, Runtime.Definition, operation.RecordingEnabled, operation.TransmitEncrypted,
            presentedReceive ? PresentedStreamId : Runtime.StreamId,
            presentedReceive ? PresentedSourceId : Runtime.SourceId);
    }
    public ChannelDefinition Identity { get; }
    public ChannelId Id => new(Identity.SessionId);
    public ChannelRuntime Runtime { get; }
    public ChannelReceiveState Receive { get; }
    public ChannelOperatorState Operator { get; }
}

/// <summary>One system's immutable route table and mutable receive lifecycle.</summary>
public sealed class ConsoleReceiveRouteState
{
    public ConsoleReceiveRouteState(IEnumerable<ChannelDefinition> channels)
    {
        Snapshot = ReceiveRouteSnapshot.Create(version: 1, channels);
        Runtime = new ReceiveRouteRuntime(Snapshot);
    }

    public ReceiveRouteSnapshot Snapshot { get; }
    public ReceiveRouteRuntime Runtime { get; }
}

/// <summary>
/// Prepares validated topology and channel state before endpoints are opened.
/// Configuration edits cannot mutate an existing session's definitions.
/// </summary>
public sealed class ConsoleSessionState
{
    private ConsoleSessionState(ConsoleTopologySnapshot topology,
        ImmutableDictionary<ChannelId, ConsoleChannelState> channels,
        ImmutableDictionary<SystemId, RadioAliasIndex> aliases,
        IReadOnlyDictionary<SystemId, P25KeyRequestState>? keyRequests = null,
        SessionTerminalFence? terminal = null)
    {
        Terminal = terminal ?? new SessionTerminalFence();
        Topology = topology;
        Channels = channels;
        Aliases = aliases;
        Media = new(channels.Values.Select(channel => (channel,
            aliases[SystemId.FromName(channel.Runtime.Definition.SystemName)])));
        KeyRequests = topology.Systems.ToImmutableDictionary(system => system.Id,
            system => keyRequests?.GetValueOrDefault(system.Id) ?? new P25KeyRequestState());
        ConsoleChannelState.LinkReceivePeers(topology.Channels.Select(channel => channels[channel.Id]));
        ILookup<SystemId, ConsoleChannelState> bySystem = topology.Channels.Select(channel => channels[channel.Id]).ToLookup(
            channel => SystemId.FromName(channel.Runtime.Definition.SystemName));
        ReceiveRoutes = topology.Systems.ToImmutableDictionary(system => system.Id,
            system => new ConsoleReceiveRouteState(bySystem[system.Id].Select(channel => channel.Identity)));
    }

    public ConsoleSessionStatus Status { get; } = new();

    public ConsoleTopologySnapshot Topology { get; }
    public ImmutableDictionary<ChannelId, ConsoleChannelState> Channels { get; }
    public ImmutableDictionary<SystemId, RadioAliasIndex> Aliases { get; }
    public ConsoleChannelMediaDirectory Media { get; }

    public ImmutableArray<ConsoleSnapshotChannel> CreateSnapshotChannels(
        IP25KeyResolver? p25Keys = null, IDmrKeyResolver? dmrKeys = null, INxdnKeyResolver? nxdnKeys = null)
        => Topology.Channels.Select(descriptor =>
        {
            ConsoleChannelState channel = Channels[descriptor.Id];
            return new ConsoleSnapshotChannel(channel, Aliases[descriptor.SystemId],
                new ChannelConfigurationAccess(channel.Runtime.Definition, p25Keys, dmrKeys, nxdnKeys));
        }).ToImmutableArray();
    public ImmutableDictionary<SystemId, ConsoleReceiveRouteState> ReceiveRoutes { get; }
    public ImmutableDictionary<SystemId, P25KeyRequestState> KeyRequests { get; }
    public ConsoleCallHistory History { get; } = new();
    public ConsoleExecutionPolicy Execution { get; } = new();
    public ReceiveMuteState ReceiveMute { get; } = new();
    internal ReceiveCallEpisodeTracker ReceiveEpisodes { get; } = new();
    internal SessionTerminalFence Terminal { get; }
    internal PttActivationArbiter ManualTransmitOwnership { get; } = new();

    // Compatibility for already constructed desktop shells. This adopts existing
    // identities and state; it never validates a document or creates radio services.
    internal static ConsoleSessionState AdoptExisting(ConsoleTopologySnapshot topology,
        IReadOnlyList<ConsoleSnapshotChannel> channels,
        IReadOnlyDictionary<SystemId, P25KeyRequestState> keyRequests, SessionTerminalFence terminal)
    {
        var aliases = channels.GroupBy(channel => SystemId.FromName(channel.State.Runtime.Definition.SystemName))
            .ToImmutableDictionary(group => group.Key, group => group.First().Aliases);
        foreach (SystemDescriptor system in topology.Systems)
            if (!aliases.ContainsKey(system.Id)) aliases = aliases.Add(system.Id, new RadioAliasIndex(null));
        return new(topology, channels.ToImmutableDictionary(channel => channel.State.Id, channel => channel.State),
            aliases, keyRequests, terminal);
    }

    public static ConsoleSessionState Create(ConsoleConfiguration configuration,
        ConfigurationReference? reference = null, IReadOnlyCollection<string>? callPrioritySystemNames = null)
    {
        ConsoleTopologySnapshot topology = ConsoleTopologyFactory.Create(configuration, reference, callPrioritySystemNames);
        var descriptors = topology.Channels.ToDictionary(channel => channel.Id);
        var channels = ImmutableDictionary.CreateBuilder<ChannelId, ConsoleChannelState>();
        foreach (ChannelConfiguration channel in configuration.Zones.SelectMany(zone => zone.Channels))
        {
            var state = new ConsoleChannelState(ChannelRuntimeDefinition.FromConfiguration(channel));
            state.Operator.SetHasCallPriority(descriptors[state.Id].AllowsTransmitDuringReceive);
            channels.Add(state.Id, state);
        }
        var aliases = configuration.Systems.ToImmutableDictionary(system => SystemId.FromName(system.Name),
            system => new RadioAliasIndex(system.RidAlias));
        return new(topology, channels.ToImmutable(), aliases);
    }
}
