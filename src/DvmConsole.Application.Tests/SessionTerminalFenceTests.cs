// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class SessionTerminalFenceTests
{
    [Fact]
    public void ReplacementAdmissionCanResumeButFinalRetirementCannot()
    {
        var fence = new SessionTerminalFence();
        var admission = new ConsoleSessionAdmission(fence);
        Assert.False(admission.IsSuppressed);
        admission.Suspend();
        admission.Suspend();
        Assert.True(admission.IsSuppressed);
        Assert.False(fence.IsClosed);
        Assert.True(admission.TryResume());
        Assert.False(admission.IsSuppressed);
        admission.Close();
        Assert.True(fence.IsClosed);
        Assert.False(admission.TryResume());
        Assert.True(admission.IsSuppressed);
        admission.Suspend();
        Assert.False(admission.TryResume());
    }

    [Fact]
    public void ClosingTheFenceIsIdempotentAndRejectsLatePublication()
    {
        var fence = new SessionTerminalFence();
        int publications = 0;

        Assert.True(fence.TryRun(() => publications++));
        Assert.True(fence.TryClose());
        Assert.False(fence.TryClose());
        Assert.False(fence.TryRun(() => publications++));

        Assert.Equal(1, publications);
        Assert.True(fence.IsClosed);
    }
}
