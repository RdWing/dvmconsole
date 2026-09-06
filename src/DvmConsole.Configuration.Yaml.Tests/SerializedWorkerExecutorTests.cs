// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace DvmConsole.Configuration.Yaml.Tests;

public sealed class SerializedWorkerExecutorTests
{
    [Fact]
    public async Task CancellationAfterGateAcquisitionDoesNotStrandTheExecutor()
    {
        var executor = new SerializedWorkerExecutor();
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            executor.RunAsync(
                token =>
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                },
                cancellation.Token));

        bool nextOperationRan = false;
        await executor.RunAsync(
            _ => nextOperationRan = true,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(nextOperationRan);
    }

    [Fact]
    public async Task SynchronousWorkDoesNotInheritTheCallerSynchronizationContext()
    {
        var executor = new SerializedWorkerExecutor();
        var callerContext = new SynchronizationContext();
        SynchronizationContext? observedContext = callerContext;
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(callerContext);
        try
        {
            await executor.RunAsync(
                _ => observedContext = SynchronizationContext.Current,
                CancellationToken.None);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        Assert.Null(observedContext);
    }
}
