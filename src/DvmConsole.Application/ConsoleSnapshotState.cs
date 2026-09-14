// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Runtime;

namespace DvmConsole.Application;

public sealed record ConsoleSnapshotChannel(ConsoleChannelState State,
    RadioAliasIndex Aliases, ChannelConfigurationAccess Access);

/// <summary>Session-owned immutable control projection and incremental update tracking.</summary>
public sealed partial class ConsoleSnapshotState : IDisposable
{
    private readonly object sync = new();
    private readonly IReadOnlyDictionary<ChannelId, ConsoleSnapshotChannel> channels;
    private readonly Func<IReadOnlyList<ChannelId>, IReadOnlyDictionary<ChannelId, ChannelSnapshotContext>> captureContext;
    private readonly Func<string> status;
    private readonly HashSet<ChannelId> dirty = [];
    private ConsoleRuntimeSnapshot? cached;
    private ConsoleRuntimeSnapshot? previous;
    private IReadOnlyDictionary<ChannelId, ChannelControlSnapshot>? updateBase;
    private ChannelId[]? updatedIds;
    private bool allDirty = true;

    public ConsoleSnapshotState(ConsoleTopologySnapshot topology,
        IEnumerable<ConsoleSnapshotChannel> channels,
        Func<IReadOnlyList<ChannelId>, IReadOnlyDictionary<ChannelId, ChannelSnapshotContext>> captureContext,
        Func<string> status)
    {
        Topology = topology ?? throw new ArgumentNullException(nameof(topology));
        this.channels = channels.ToImmutableDictionary(channel => channel.State.Id);
        this.captureContext = captureContext ?? throw new ArgumentNullException(nameof(captureContext));
        this.status = status ?? throw new ArgumentNullException(nameof(status));
    }

    public ConsoleTopologySnapshot Topology { get; }

    public void Invalidate(ChannelId? channel = null, bool allChannels = false, bool includeChannels = true)
    {
        lock (sync)
        {
            cached = null;
            if (!includeChannels) return;
            if (allChannels || channel is null) allDirty = true;
            else if (!allDirty) dirty.Add(channel.Value);
        }
    }

    public ConsoleRuntimeSnapshot Capture()
    {
        lock (sync)
        {
            if (cached is not null) return cached;
            ChannelId[]? changed = allDirty ? null : dirty.ToArray();
            ChannelId[] projected = previous is null || changed is null
                ? channels.Keys.ToArray() : changed.Where(channels.ContainsKey).ToArray();
            IReadOnlyDictionary<ChannelId, ChannelSnapshotContext> contexts = captureContext(projected);
            updateBase = previous?.Channels;
            updatedIds = changed;
            var snapshots = UpdateProjection(previous?.Channels, projected, id =>
            {
                ConsoleSnapshotChannel channel = channels[id];
                return ConsoleChannelSnapshotProjector.Capture(channel.State, channel.Aliases, channel.Access,
                    contexts.GetValueOrDefault(id) ?? new ChannelSnapshotContext());
            });
            cached = new ConsoleRuntimeSnapshot(0, Topology.Configuration, snapshots, false, status());
            previous = cached;
            allDirty = false;
            dirty.Clear();
            return cached;
        }
    }

    public ConsoleSnapshotUpdate CaptureUpdate(ConsoleRuntimeSnapshot baseline)
    {
        lock (sync)
        {
            ConsoleRuntimeSnapshot current = Capture();
            return new(current, ReferenceEquals(baseline.Channels, current.Channels) ? [] :
                ReferenceEquals(baseline.Channels, updateBase) ? updatedIds : null);
        }
    }

    internal static ImmutableDictionary<TKey, TValue> UpdateProjection<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue>? previous, IEnumerable<TKey> dirtyKeys, Func<TKey, TValue> project)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(dirtyKeys);
        ArgumentNullException.ThrowIfNull(project);
        ImmutableDictionary<TKey, TValue> current = previous switch
        {
            null => ImmutableDictionary<TKey, TValue>.Empty,
            ImmutableDictionary<TKey, TValue> immutable => immutable,
            _ => previous.ToImmutableDictionary()
        };
        foreach (TKey key in dirtyKeys) current = current.SetItem(key, project(key));
        return current;
    }
}
