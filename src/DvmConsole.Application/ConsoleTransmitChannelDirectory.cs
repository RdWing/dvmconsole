// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Frozen;
using System.Collections.Immutable;
using DvmConsole.Media;

namespace DvmConsole.Application;

/// <summary>Session-owned transmit lookups that read live operator and key state.</summary>
public sealed class ConsoleTransmitChannelDirectory
{
    private readonly (ConsoleChannelState State, ChannelConfigurationAccess Access)[] orderedChannels;
    private readonly FrozenDictionary<ChannelId, (ConsoleChannelState State, ChannelConfigurationAccess Access)> channels;

    public ConsoleTransmitChannelDirectory(
        IEnumerable<(ConsoleChannelState State, ChannelConfigurationAccess Access)> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        orderedChannels = channels.ToArray();
        this.channels = orderedChannels.ToFrozenDictionary(channel => channel.State.Id);
        ChannelIds = orderedChannels.Select(channel => channel.State.Id).ToImmutableArray();
    }

    public ConsoleTransmitChannelDirectory(IEnumerable<ConsoleChannelState> channels,
        IP25KeyResolver? p25Keys = null, IDmrKeyResolver? dmrKeys = null, INxdnKeyResolver? nxdnKeys = null)
        : this(channels.Select(channel => (channel,
            new ChannelConfigurationAccess(channel.Runtime.Definition, p25Keys, dmrKeys, nxdnKeys))))
    {
    }

    public ImmutableArray<ChannelId> ChannelIds { get; }

    internal ConsoleChannelState State(ChannelId id) => channels[id].State;

    internal bool CanChangeEncryption(ChannelId id, bool encrypted)
    {
        var channel = channels[id];
        return channel.Access.CanChangeEncryption(channel.State.Operator.Snapshot, encrypted);
    }

    /// <summary>Captures intent without dropping temporarily unavailable targets; admission validates them later.</summary>
    public ChannelId[] SelectManualChannels(IEnumerable<ChannelId>? scope = null)
        => (scope ?? ChannelIds).Where(id => channels[id].State.Operator.Snapshot.TransmitSelected)
            .Distinct().ToArray();

    public bool CanListen(ChannelId id) => channels[id].Access.CanListen;

    public bool CanTransmitByConfiguration(ChannelId id)
    {
        var channel = channels[id];
        return channel.Access.CanTransmit(channel.State.Operator.Snapshot.TransmitEncrypted);
    }

    public bool CanTransmit(ChannelId id)
        => CanTransmitByConfiguration(id) && channels[id].State.Authority != TargetAuthorityState.Unavailable;

    public ChannelId[] SelectToneChannels(ConsoleToneTargets targets, IEnumerable<ChannelId>? scope = null)
    {
        if (!Enum.IsDefined(targets)) throw new ArgumentOutOfRangeException(nameof(targets));
        return (scope ?? ChannelIds).Where(id =>
        {
            var selection = channels[id].State.Operator.Snapshot;
            return targets == ConsoleToneTargets.Page ? selection.PageSelected : selection.AlertSelected;
        }).Distinct().ToArray();
    }

    /// <summary>Full shutdown includes both coordinator-owned capture and retained channel TX state.</summary>
    public ChannelId[] CaptureShutdownChannels(IEnumerable<ChannelId> activeCapture)
        => activeCapture.Concat(orderedChannels.Where(channel => channel.State.Operator.Snapshot.TransmitEnabled)
            .Select(channel => channel.State.Id)).Distinct().ToArray();

    public bool HasStartingTransmit(IEnumerable<ChannelId> scope)
        => scope.Any(id => channels[id].State.Operator.Snapshot.TransmitStarting);

    public bool OwnsActiveTransmit(IEnumerable<ChannelId> scope, IReadOnlyCollection<ChannelId> activeCapture)
        => scope.Any(id => channels[id].State.Operator.Snapshot.TransmitEnabled || activeCapture.Contains(id));

    public TransmitChannelDescriptor Capture(ChannelId id)
    {
        var channel = channels[id];
        return channel.State.CaptureTransmitDescriptor(channel.Access);
    }

    public TransmitChannelDescriptor? Find(ChannelId id)
        => channels.TryGetValue(id, out var channel)
            ? channel.State.CaptureTransmitDescriptor(channel.Access) : null;

    public PatchMemberCapabilities CapturePatchCapabilities(ChannelId id)
    {
        var channel = channels[id];
        bool encrypted = channel.State.Operator.Snapshot.TransmitEncrypted;
        return new(channel.State.Runtime.Definition.Name, channel.Access.CanListen,
            channel.Access.CanTransmit(encrypted) && channel.State.Authority != TargetAuthorityState.Unavailable);
    }

    public IReadOnlyList<TransmitChannelDescriptor> CaptureAll()
        => orderedChannels.Select(channel => channel.State.CaptureTransmitDescriptor(channel.Access)).ToArray();
}
