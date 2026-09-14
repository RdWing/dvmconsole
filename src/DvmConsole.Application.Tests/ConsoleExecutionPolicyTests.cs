// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ConsoleExecutionPolicyTests
{
    [Fact]
    public void LeavingConsoleReleasesManualIntentWithoutChangingForegroundOrPatches()
    {
        var policy = new ConsoleExecutionPolicy();
        var manual = policy.TryAcquire(ConsoleTransmitIntent.Manual)!.Value;
        var patch = policy.TryAcquire(ConsoleTransmitIntent.AutomaticPatch)!.Value;
        policy.SetManualControlsAvailable(false);
        Assert.Equal(ConsoleExecutionState.Foreground, policy.Snapshot.State);
        Assert.False(policy.IsCurrent(manual));
        Assert.True(policy.IsCurrent(patch));
        Assert.NotNull(policy.TryAcquire(ConsoleTransmitIntent.Tone));
        policy.SetManualControlsAvailable(true);
        Assert.False(policy.IsCurrent(manual));
        Assert.NotNull(policy.TryAcquire(ConsoleTransmitIntent.Manual));
    }

    [Fact]
    public void BackgroundRetiresManualIntentButPreservesAnOngoingPatch()
    {
        var policy = new ConsoleExecutionPolicy();
        var manual = policy.TryAcquire(ConsoleTransmitIntent.Manual)!.Value;
        var tone = policy.TryAcquire(ConsoleTransmitIntent.Tone)!.Value;
        var patch = policy.TryAcquire(ConsoleTransmitIntent.AutomaticPatch)!.Value;
        policy.SetForeground(false);
        Assert.True(policy.Snapshot.CanReceive);
        Assert.False(policy.IsCurrent(manual));
        Assert.False(policy.IsCurrent(tone));
        Assert.True(policy.IsCurrent(patch));
        Assert.Null(policy.TryAcquire(ConsoleTransmitIntent.Manual));
        policy.SetForeground(true);
        Assert.False(policy.IsCurrent(manual));
        Assert.False(policy.IsCurrent(tone));
        Assert.True(policy.IsCurrent(patch));
    }

    [Fact]
    public void RecoveryAdmitsNewCallsWithoutReplayingInterruptedCalls()
    {
        var policy = new ConsoleExecutionPolicy();
        var patch = policy.TryAcquire(ConsoleTransmitIntent.AutomaticPatch)!.Value;
        policy.Interrupt();
        Assert.False(policy.IsCurrent(patch));
        Assert.Null(policy.TryAcquire(ConsoleTransmitIntent.AutomaticPatch));
        var attempt = policy.BeginRecovery(false)!.Value;
        Assert.Null(policy.BeginRecovery(false));
        policy.SetForeground(false);
        Assert.True(policy.CompleteRecovery(attempt, true));
        Assert.Equal(ConsoleExecutionState.BackgroundListening, policy.Snapshot.State);
        Assert.False(policy.IsCurrent(patch));
        Assert.NotNull(policy.TryAcquire(ConsoleTransmitIntent.AutomaticPatch));
        Assert.Null(policy.TryAcquire(ConsoleTransmitIntent.Tone));
        Assert.False(policy.CompleteRecovery(attempt, true));
    }

    [Fact]
    public void ResetRequiresExplicitResumeAndRejectsLateRecoveryCompletion()
    {
        var policy = new ConsoleExecutionPolicy();
        policy.Interrupt();
        var stale = policy.BeginRecovery(false)!.Value;
        policy.MediaServicesReset();
        policy.Interrupt();
        Assert.Null(policy.BeginRecovery(false));
        Assert.False(policy.CompleteRecovery(stale, true));
        var fresh = policy.BeginRecovery(true)!.Value;
        Assert.True(policy.CompleteRecovery(fresh, false));
        Assert.Equal(ConsoleExecutionState.RequiresResume, policy.Snapshot.State);
        Assert.Null(policy.BeginRecovery(false));
    }

    [Fact]
    public void StopIsTerminalIncludingLateRecoveryAndForegroundEvents()
    {
        var policy = new ConsoleExecutionPolicy();
        policy.Interrupt();
        var pending = policy.BeginRecovery(false)!.Value;
        var stopped = policy.Stop();
        Assert.Equal(stopped, policy.SetForeground(false));
        Assert.Equal(stopped, policy.MediaServicesReset());
        Assert.False(policy.CompleteRecovery(pending, true));
        Assert.Null(policy.BeginRecovery(true));
        Assert.False(policy.Snapshot.CanReceive);
    }
}
