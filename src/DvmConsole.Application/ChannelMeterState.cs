// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>Latest meter values, independent of control snapshots and UI notifications.</summary>
internal sealed class ChannelMeterState(ChannelId channelId)
{
    private readonly object sync = new();
    private ChannelAudioMeterLevels levels;
    public ChannelId ChannelId { get; } = channelId;
    public ChannelAudioMeterLevels Snapshot { get { lock (sync) return levels; } }
    public event EventHandler<ChannelAudioMeterLevels>? Changed;

    // Callers serialize publication with their meter work. A zero reset always
    // wins over the optional display threshold, including tiny remaining tails.
    public void Update(double level, double peak, double minimumChange = 0)
    {
        ChannelAudioMeterLevels current;
        lock (sync)
        {
            bool levelChanged = HasChanged(levels.Rms, level, minimumChange);
            bool peakChanged = HasChanged(levels.Peak, peak, minimumChange);
            if (!levelChanged && !peakChanged) return;
            current = levels = new(levelChanged ? level : levels.Rms, peakChanged ? peak : levels.Peak);
        }
        foreach (EventHandler<ChannelAudioMeterLevels> observer in Changed?.GetInvocationList() ?? [])
        {
            try { observer(this, current); }
            catch { /* Presentation cannot prevent other consumers from receiving a meter edge. */ }
        }
    }

    private static bool HasChanged(double previous, double current, double minimumChange)
        => current == 0 ? previous != 0 : previous != current && Math.Abs(previous - current) >= minimumChange;
}
