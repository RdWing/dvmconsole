// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>Keeps operational context live independently of presentation subscriptions.</summary>
internal sealed class ConsoleSnapshotContextObserver : IDisposable
{
    private readonly ConsoleSnapshotState snapshots;
    private readonly ReceiveMuteState mute;
    private readonly RecordingPlaybackChannelState playback;
    private readonly IReceiveRecordingSession? recordings;
    private P25KeyRetrievalCoordinator? keys;
    private readonly object playbackSync = new();
    private ChannelId? previousPlayback;
    private int disposed;

    public ConsoleSnapshotContextObserver(ConsoleSnapshotState snapshots, ReceiveMuteState mute,
        RecordingPlaybackChannelState playback, IReceiveRecordingSession? recordings,
        P25KeyRetrievalCoordinator? keys)
    {
        this.snapshots = snapshots;
        this.mute = mute;
        this.playback = playback;
        this.recordings = recordings;
        this.keys = keys;
        previousPlayback = playback.Snapshot.Channel;
        mute.Changed += AllChanged;
        playback.Changed += PlaybackChanged;
        if (recordings is not null) recordings.StateChanged += RecordingChanged;
        if (keys is not null) keys.KeysChanged += AllChanged;
    }

    public void BindKeys(P25KeyRetrievalCoordinator value)
    {
        lock (playbackSync)
        {
            if (Volatile.Read(ref disposed) != 0 || ReferenceEquals(keys, value)) return;
            if (keys is not null) keys.KeysChanged -= AllChanged;
            keys = value;
            keys.KeysChanged += AllChanged;
        }
        AllChanged(this, EventArgs.Empty);
    }

    private void AllChanged(object? sender, EventArgs args)
    {
        if (Volatile.Read(ref disposed) == 0) snapshots.NotifyContextChanged();
    }
    private void RecordingChanged(ChannelId id)
    {
        if (Volatile.Read(ref disposed) == 0) snapshots.NotifyContextChanged([id]);
    }

    private void PlaybackChanged(object? sender, EventArgs args)
    {
        ChannelId[] affected;
        lock (playbackSync)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            ChannelId? current = playback.Snapshot.Channel;
            if (previousPlayback == current) return;
            ChannelId? previous = previousPlayback;
            previousPlayback = current;
            affected = previous is { } oldChannel
                ? current is { } newChannel ? [oldChannel, newChannel] : [oldChannel]
                : current is { } channel ? [channel] : [];
        }
        snapshots.NotifyContextChanged(affected);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        mute.Changed -= AllChanged;
        playback.Changed -= PlaybackChanged;
        if (recordings is not null) recordings.StateChanged -= RecordingChanged;
        lock (playbackSync)
        {
            if (keys is not null) keys.KeysChanged -= AllChanged;
            keys = null;
        }
    }
}
