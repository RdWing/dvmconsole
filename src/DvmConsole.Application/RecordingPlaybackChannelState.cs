// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

public sealed record RecordingPlaybackChannelSnapshot(long Revision = 0,
    RecordingId? Recording = null, ChannelId? Channel = null);

/// <summary>Tracks playback identity independently of delayed history-row updates.</summary>
public sealed class RecordingPlaybackChannelState
{
    private readonly object sync = new();
    private RecordingPlaybackChannelSnapshot snapshot = new();
    public RecordingPlaybackChannelSnapshot Snapshot => Volatile.Read(ref snapshot);
    public event EventHandler? Changed;

    public RecordingPlaybackChannelSnapshot Apply(RecordingId recording, bool playing, ChannelId? channel)
    {
        RecordingPlaybackChannelSnapshot next;
        lock (sync)
        {
            if (!playing && snapshot.Recording != recording) return snapshot;
            next = new(snapshot.Revision + 1, playing ? recording : null, playing ? channel : null);
            Volatile.Write(ref snapshot, next);
        }
        PublishChanged();
        return next;
    }

    /// <summary>Completes legacy metadata lookup only while its original playback is current.</summary>
    public void ResolveChannel(long revision, ChannelId? channel)
    {
        lock (sync)
        {
            if (snapshot.Revision != revision || snapshot.Recording is null || snapshot.Channel == channel) return;
            Volatile.Write(ref snapshot, snapshot with { Channel = channel });
        }
        PublishChanged();
    }

    public void Clear()
    {
        lock (sync)
        {
            if (snapshot.Recording is null) return;
            Volatile.Write(ref snapshot, new(snapshot.Revision + 1));
        }
        PublishChanged();
    }

    private void PublishChanged()
    {
        foreach (EventHandler observer in Changed?.GetInvocationList() ?? [])
        {
            try { observer(this, EventArgs.Empty); }
            catch { /* Playback state cannot depend on presentation observers. */ }
        }
    }
}
