// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Frozen;
using DvmConsole.Core.Configuration;

namespace DvmConsole.Application;

/// <summary>Session-stable media lookups without a dependency on channel presentation.</summary>
public sealed class ConsoleChannelMediaDirectory
{
    private readonly FrozenDictionary<ChannelId, (ConsoleChannelState State, RadioAliasIndex Aliases)> channels;

    public ConsoleChannelMediaDirectory(IEnumerable<(ConsoleChannelState State, RadioAliasIndex Aliases)> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        this.channels = channels.ToFrozenDictionary(channel => channel.State.Id);
    }

    public ConsoleChannelState State(ChannelId id) => channels[id].State;
    public ReceiveChannelDescriptor DescribeReceive(ChannelId id) => new(id, State(id).Runtime.Definition);
    public string LastCallerText(ChannelId id)
        => ConsoleChannelSnapshotProjector.LastCallerText(State(id), channels[id].Aliases);
    public double Gain(ChannelId id) => State(id).Operator.Snapshot.Gain;
    public double Balance(ChannelId id) => State(id).Operator.Snapshot.Balance;
    public bool ShouldRecord(ChannelId id, uint subscriber) => State(id).RecordingSubscribers.Allows(subscriber);
    public string SubscriberAlias(ChannelId id, uint subscriber) => channels[id].Aliases.Find(subscriber);
    public ChannelRecordingDescriptor DescribeRecording(ChannelId id) => State(id).CaptureRecordingDescriptor();
}
