// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ConsoleTransmitShutdownTests
{
    [Fact]
    public async Task InputFailureStillClearsLatchesAndReleasesBothGatesDuringPartialConstruction()
    {
        var runtime = new ConsoleTransmitRuntime();
        using var commands = new SemaphoreSlim(1, 1);
        using var admission = new SemaphoreSlim(1, 1);
        var channels = new ConsoleTransmitChannelDirectory(Array.Empty<ConsoleChannelState>());
        var failure = new IOException("Input stop failed");
        bool cleared = false;
        var actual = await Assert.ThrowsAsync<IOException>(() => runtime.ReleaseForShutdownAsync(
            commands, admission, channels, _ => ValueTask.FromException(failure), () => cleared = true));
        Assert.Same(failure, actual);
        Assert.True(cleared);
        Assert.Equal(1, commands.CurrentCount);
        Assert.Equal(1, admission.CurrentCount);
    }

    [Fact]
    public async Task CancelledAdmissionWaitReturnsOnlyTheAcquiredGate()
    {
        var runtime = new ConsoleTransmitRuntime();
        using var commands = new SemaphoreSlim(1, 1);
        using var admission = new SemaphoreSlim(0, 1);
        using var cancellation = new CancellationTokenSource();
        var channels = new ConsoleTransmitChannelDirectory(Array.Empty<ConsoleChannelState>());
        bool cleared = false;
        Task release = runtime.ReleaseForShutdownAsync(commands, admission, channels,
            _ => ValueTask.CompletedTask, () => cleared = true, cancellation.Token);
        Assert.Equal(0, commands.CurrentCount);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => release);
        Assert.False(cleared);
        Assert.Equal(1, commands.CurrentCount);
        Assert.Equal(0, admission.CurrentCount);
    }
}
