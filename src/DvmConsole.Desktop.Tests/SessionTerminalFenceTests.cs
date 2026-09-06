// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class SessionTerminalFenceTests
{
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
