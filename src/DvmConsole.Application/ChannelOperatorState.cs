// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>Operator intent and audio transitions, separate from packet/media state.</summary>
public sealed record ChannelOperatorSnapshot(
    bool AudioEnabled = false,
    bool AudioSuspended = false,
    bool TransmitEnabled = false,
    bool TransmitStarting = false,
    bool TransmitStopping = false,
    bool TransmitSelected = false,
    bool PageSelected = false,
    bool AlertSelected = false,
    bool TransmitEncrypted = false,
    bool HasCallPriority = false,
    bool RecordingEnabled = false,
    double Gain = 1.0,
    double Balance = 0,
    string OutputRoute = "");

/// <summary>
/// Owns a channel's operator state. Readers retain immutable snapshots; compound
/// audio and transmit transitions publish together, never as intermediate flags.
/// Presentation adapters remain responsible for their own UI notifications.
/// </summary>
public sealed class ChannelOperatorState(bool transmitEncrypted = false)
{
    private readonly object sync = new();
    private ChannelOperatorSnapshot snapshot = new(TransmitEncrypted: transmitEncrypted);

    public ChannelOperatorSnapshot Snapshot => Volatile.Read(ref snapshot);
    public event EventHandler<ChannelOperatorSnapshot>? Changed;

    public bool SetAudioEnabled(bool enabled)
        => Update(current => current with { AudioEnabled = enabled, AudioSuspended = false });

    public bool SetAudioSuspended(bool suspended)
        => Update(current => current.AudioEnabled
            ? current with { AudioSuspended = suspended }
            : current);

    public bool SetTransmitEnabled(bool enabled)
        => Update(current => current with { TransmitEnabled = enabled });

    public bool SetTransmitTransition(bool starting, bool stopping)
    {
        if (starting && stopping)
            throw new ArgumentException("Transmit cannot start and stop simultaneously.");
        return Update(current => current with { TransmitStarting = starting, TransmitStopping = stopping });
    }

    public bool SetTransmitSelected(bool selected)
        => Update(current => current with { TransmitSelected = selected });
    public bool SetPageSelected(bool selected)
        => Update(current => current with { PageSelected = selected });
    public bool SetAlertSelected(bool selected)
        => Update(current => current with { AlertSelected = selected });
    public bool SetTransmitEncrypted(bool encrypted)
        => Update(current => current with { TransmitEncrypted = encrypted });
    public bool SetHasCallPriority(bool enabled)
        => Update(current => current with { HasCallPriority = enabled });
    public bool SetRecordingEnabled(bool enabled)
        => Update(current => current with { RecordingEnabled = enabled });

    internal static double NormalizeGain(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 4) : 1.0;
    internal static double NormalizeBalance(double value) => double.IsFinite(value) ? Math.Clamp(value, -1, 1) : 0;

    public bool SetGain(double value)
    {
        double gain = NormalizeGain(value);
        return Update(current => Math.Abs(current.Gain - gain) < 0.0001
            ? current : current with { Gain = gain });
    }

    public bool SetBalance(double value)
    {
        double balance = NormalizeBalance(value);
        return Update(current => Math.Abs(current.Balance - balance) < 0.0001
            ? current : current with { Balance = balance });
    }

    public bool SetOutputRoute(string? route)
        => Update(current => current with { OutputRoute = route ?? string.Empty });

    private bool Update(Func<ChannelOperatorSnapshot, ChannelOperatorSnapshot> change)
    {
        ChannelOperatorSnapshot next;
        lock (sync)
        {
            next = change(snapshot);
            if (next == snapshot)
                return false;
            Volatile.Write(ref snapshot, next);
        }
        foreach (EventHandler<ChannelOperatorSnapshot> observer in Changed?.GetInvocationList() ?? [])
        {
            try { observer(this, next); }
            catch
            {
                // Committed state must remain available to later observers.
            }
        }
        return true;
    }
}
