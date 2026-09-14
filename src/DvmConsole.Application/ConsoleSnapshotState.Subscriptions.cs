// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using DvmConsole.Core.Runtime;

namespace DvmConsole.Application;

public sealed partial class ConsoleSnapshotState
{
    private readonly List<ChannelSubscription> subscriptions = [];
    private ConsoleSessionStatus? sessionStatus;
    private int disposed;

    /// <summary>Observes operational state directly; host dispatch is never an input.</summary>
    public ConsoleSnapshotState(ConsoleTopologySnapshot topology,
        IEnumerable<ConsoleSnapshotChannel> channels,
        Func<IReadOnlyList<ChannelId>, IReadOnlyDictionary<ChannelId, ChannelSnapshotContext>> captureContext,
        ConsoleSessionStatus status)
        : this(topology, channels, captureContext, () => status.Snapshot.Console)
    {
        sessionStatus = status ?? throw new ArgumentNullException(nameof(status));
        foreach (var group in this.channels.Values.GroupBy(channel => channel.State.Identity.RouteKey))
        {
            ChannelId[] peers = group.Select(channel => channel.State.Id).ToArray();
            foreach (var channel in group)
                subscriptions.Add(new ChannelSubscription(this, channel.State, peers));
        }
        status.Changed += HandleStatusChanged;
    }

    public event EventHandler? Changed;

    internal void NotifyContextChanged(IReadOnlyList<ChannelId>? affected = null)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        lock (sync)
        {
            if (affected is null) Invalidate(allChannels: true);
            else foreach (ChannelId id in affected) Invalidate(id);
        }
        PublishChanged();
    }

    private void HandleStatusChanged(object? sender, EventArgs args)
    {
        Invalidate(includeChannels: false);
        PublishChanged();
    }

    private void ChannelsChanged(IEnumerable<ChannelId> ids)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        lock (sync)
            foreach (ChannelId id in ids) Invalidate(id);
        PublishChanged();
    }

    private void PublishChanged()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        foreach (EventHandler observer in Changed?.GetInvocationList() ?? [])
        {
            try { observer(this, EventArgs.Empty); }
            catch { /* Committed runtime state remains available to other observers. */ }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (sessionStatus is not null) sessionStatus.Changed -= HandleStatusChanged;
        foreach (var subscription in subscriptions) subscription.Dispose();
        subscriptions.Clear();
    }

    private sealed class ChannelSubscription : IDisposable
    {
        private readonly ConsoleSnapshotState owner;
        private readonly ConsoleChannelState channel;
        private readonly ChannelId[] peers;

        public ChannelSubscription(ConsoleSnapshotState owner, ConsoleChannelState channel, ChannelId[] peers)
        {
            this.owner = owner;
            this.channel = channel;
            this.peers = peers;
            channel.Operator.Changed += OperatorChanged;
            channel.Receive.Changed += ReceiveChanged;
            channel.AuthorityChanged += AuthorityChanged;
            channel.Runtime.PropertyChanged += RuntimeChanged;
        }

        private void OperatorChanged(object? sender, ChannelOperatorSnapshot snapshot) => owner.ChannelsChanged(peers);
        private void ReceiveChanged(object? sender, EventArgs args) => owner.ChannelsChanged(peers);
        private void AuthorityChanged(object? sender, EventArgs args) => owner.ChannelsChanged([channel.Id]);
        private void RuntimeChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName != nameof(ChannelRuntime.LastActivity)) owner.ChannelsChanged(peers);
        }

        public void Dispose()
        {
            channel.Operator.Changed -= OperatorChanged;
            channel.Receive.Changed -= ReceiveChanged;
            channel.AuthorityChanged -= AuthorityChanged;
            channel.Runtime.PropertyChanged -= RuntimeChanged;
        }
    }
}
