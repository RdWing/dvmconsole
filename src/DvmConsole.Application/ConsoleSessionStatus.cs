// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

public sealed record ConsoleSessionStatusSnapshot(
    string Console = "", string Audio = "RX audio disabled.", string Transmit = "PTT idle.", string Latest = "");

/// <summary>Authoritative session messages, independent of host presentation and dispatch.</summary>
public sealed class ConsoleSessionStatus
{
    private readonly object sync = new();
    private ConsoleSessionStatusSnapshot snapshot = new();
    public ConsoleSessionStatusSnapshot Snapshot => Volatile.Read(ref snapshot);
    public event EventHandler? Changed;

    public void SetConsole(string text) => Update(text, static (current, value) =>
        current.Console == value ? current : current with { Console = value, Latest = value });
    public void SetAudio(string text) => Update(text, static (current, value) =>
        current.Audio == value ? current : current with { Audio = value, Latest = value });
    public void SetTransmit(string text) => Update(text, static (current, value) =>
        current.Transmit == value ? current : current with { Transmit = value, Latest = value });

    private void Update(string text, Func<ConsoleSessionStatusSnapshot, string, ConsoleSessionStatusSnapshot> update)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (sync)
        {
            var next = update(snapshot, text);
            if (ReferenceEquals(next, snapshot)) return;
            Volatile.Write(ref snapshot, next);
        }
        // Observers read the latest snapshot, so concurrent or reentrant publication
        // cannot replay an older message. No host callback runs under the state lock.
        foreach (EventHandler observer in Changed?.GetInvocationList() ?? [])
        {
            try { observer(this, EventArgs.Empty); }
            catch { /* One presentation observer cannot prevent later subscribers. */ }
        }
    }
}
