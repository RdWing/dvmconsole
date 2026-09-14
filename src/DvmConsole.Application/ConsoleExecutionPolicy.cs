// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

public enum ConsoleExecutionState { Foreground, BackgroundListening, Interrupted, Recovering, RequiresResume, Stopping }
public enum ConsoleTransmitIntent { Manual, Tone, AutomaticPatch }
public readonly record struct ConsoleExecutionLease(ConsoleTransmitIntent Intent, long Generation);
public readonly record struct ConsoleRecoveryAttempt(long Generation);
public sealed record ConsoleExecutionSnapshot(ConsoleExecutionState State, bool IsForeground,
    long ManualGeneration, long PatchGeneration, long RecoveryGeneration, bool ManualControlsAvailable = true)
{
    public bool CanReceive => State is ConsoleExecutionState.Foreground or ConsoleExecutionState.BackgroundListening;
    public bool CanTransmitManually => State == ConsoleExecutionState.Foreground && ManualControlsAvailable;
    public bool CanSendTones => State == ConsoleExecutionState.Foreground;
    public bool CanForwardPatches => CanReceive;
}

/// <summary>
/// Session admission and stale-work fencing. Hosts close admission here before
/// asynchronous cleanup. Patch leases are acquired only at new source-call boundaries;
/// surviving a background transition is allowed, surviving interruption is not.
/// </summary>
public sealed class ConsoleExecutionPolicy
{
    private readonly object sync = new();
    private ConsoleExecutionSnapshot snapshot = new(ConsoleExecutionState.Foreground, true, 0, 0, 0);
    public ConsoleExecutionSnapshot Snapshot { get { lock (sync) return snapshot; } }

    public ConsoleExecutionSnapshot SetForeground(bool foreground)
    {
        lock (sync)
        {
            if (snapshot.State == ConsoleExecutionState.Stopping || snapshot.IsForeground == foreground) return snapshot;
            snapshot = snapshot with
            {
                IsForeground = foreground,
                ManualGeneration = snapshot.ManualGeneration + 1,
                State = snapshot.CanReceive
                    ? foreground ? ConsoleExecutionState.Foreground : ConsoleExecutionState.BackgroundListening
                    : snapshot.State
            };
            return snapshot;
        }
    }

    public ConsoleExecutionSnapshot SetManualControlsAvailable(bool available)
    {
        lock (sync)
        {
            if (snapshot.State == ConsoleExecutionState.Stopping || snapshot.ManualControlsAvailable == available) return snapshot;
            snapshot = snapshot with { ManualControlsAvailable = available, ManualGeneration = snapshot.ManualGeneration + 1 };
            return snapshot;
        }
    }

    public ConsoleExecutionSnapshot Interrupt() => Suspend(ConsoleExecutionState.Interrupted);
    public ConsoleExecutionSnapshot MediaServicesReset() => Suspend(ConsoleExecutionState.RequiresResume);
    public ConsoleExecutionSnapshot Stop() => Suspend(ConsoleExecutionState.Stopping);

    private ConsoleExecutionSnapshot Suspend(ConsoleExecutionState state)
    {
        lock (sync)
        {
            if (snapshot.State == ConsoleExecutionState.Stopping || snapshot.State == state) return snapshot;
            // An interruption must not remove the explicit-resume requirement of a reset.
            if (snapshot.State == ConsoleExecutionState.RequiresResume && state == ConsoleExecutionState.Interrupted) return snapshot;
            snapshot = snapshot with
            {
                State = state,
                ManualGeneration = snapshot.ManualGeneration + 1,
                PatchGeneration = snapshot.PatchGeneration + 1,
                RecoveryGeneration = snapshot.RecoveryGeneration + 1
            };
            return snapshot;
        }
    }

    public ConsoleRecoveryAttempt? BeginRecovery(bool explicitResume)
    {
        lock (sync)
        {
            if (snapshot.State != ConsoleExecutionState.Interrupted &&
                !(snapshot.State == ConsoleExecutionState.RequiresResume && explicitResume)) return null;
            snapshot = snapshot with { State = ConsoleExecutionState.Recovering, RecoveryGeneration = snapshot.RecoveryGeneration + 1 };
            return new(snapshot.RecoveryGeneration);
        }
    }

    public bool CompleteRecovery(ConsoleRecoveryAttempt attempt, bool succeeded)
    {
        lock (sync)
        {
            if (snapshot.State != ConsoleExecutionState.Recovering || attempt.Generation != snapshot.RecoveryGeneration) return false;
            snapshot = snapshot with
            {
                State = succeeded
                ? snapshot.IsForeground ? ConsoleExecutionState.Foreground : ConsoleExecutionState.BackgroundListening
                : ConsoleExecutionState.RequiresResume
            };
            return true;
        }
    }

    public ConsoleExecutionLease? TryAcquire(ConsoleTransmitIntent intent)
    {
        lock (sync)
        {
            bool patch = intent == ConsoleTransmitIntent.AutomaticPatch;
            if (!Enum.IsDefined(intent)) throw new ArgumentOutOfRangeException(nameof(intent));
            if (!(patch ? snapshot.CanForwardPatches : intent == ConsoleTransmitIntent.Tone
                    ? snapshot.CanSendTones : snapshot.CanTransmitManually)) return null;
            return new(intent, patch ? snapshot.PatchGeneration : snapshot.ManualGeneration);
        }
    }

    public bool IsCurrent(ConsoleExecutionLease lease)
    {
        lock (sync)
        {
            return lease.Intent switch
            {
                ConsoleTransmitIntent.AutomaticPatch => snapshot.CanForwardPatches && lease.Generation == snapshot.PatchGeneration,
                ConsoleTransmitIntent.Manual => snapshot.CanTransmitManually && lease.Generation == snapshot.ManualGeneration,
                ConsoleTransmitIntent.Tone => snapshot.CanSendTones && lease.Generation == snapshot.ManualGeneration,
                _ => false
            };
        }
    }
}
