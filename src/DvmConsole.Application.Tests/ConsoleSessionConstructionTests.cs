// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ConsoleSessionConstructionTests
{
    [Fact]
    public async Task HostRollbackRetainsBothFailuresAndOriginalIdentity()
    {
        var original = new IOException("Preparation failed");
        var cleanup = new InvalidOperationException("Retirement failed");
        var failure = await Assert.ThrowsAsync<AggregateException>(() =>
            ConsoleSessionConstruction.RollbackAsync(original, () => ValueTask.FromException(cleanup)).AsTask());
        Assert.Same(original, failure.InnerExceptions[0]);
        Assert.Same(cleanup, failure.InnerExceptions[1]);
        Assert.Same(original, await Assert.ThrowsAsync<IOException>(() =>
            ConsoleSessionConstruction.RollbackAsync(original, () => ValueTask.CompletedTask).AsTask()));
    }

    [Fact]
    public async Task LateCompletionAfterCancellationRetiresRegisteredServices()
    {
        var services = new ConsoleSessionServices();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = new SessionTerminalFence();
        using var cancellation = new CancellationTokenSource();
        Task<object> construction = ConsoleSessionConstruction.CreateAsync(services, terminal, async _ =>
        {
            services.Connection.Register("prepared-radio", () =>
            {
                Assert.True(terminal.IsClosed);
                retired.SetResult();
                return ValueTask.CompletedTask;
            });
            entered.SetResult();
            await complete.Task;
            return new object();
        }, cancellation.Token).AsTask();
        await entered.Task;
        cancellation.Cancel();
        complete.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => construction);
        Assert.True(retired.Task.IsCompletedSuccessfully);
        Assert.Equal(0, services.Count);
    }

    [Fact]
    public async Task FailureRetiresAllServicesInReverseOrder()
    {
        var services = new ConsoleSessionServices();
        var retired = new List<string>();
        var expected = new InvalidOperationException("setup failed");
        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ConsoleSessionConstruction.CreateAsync<object>(services, _ =>
            {
                services.Connection.Register("radio", () => { retired.Add("radio"); return ValueTask.CompletedTask; });
                services.Audio.Register("audio", () => { retired.Add("audio"); return ValueTask.CompletedTask; });
                return ValueTask.FromException<object>(expected);
            }).AsTask());
        Assert.Same(expected, actual);
        Assert.Equal(new[] { "audio", "radio" }, retired);
    }

    [Fact]
    public async Task ConstructionAndCleanupFailuresAreBothRetained()
    {
        var services = new ConsoleSessionServices();
        var constructionFault = new InvalidOperationException("setup failed");
        var cleanupFault = new IOException("cleanup failed");
        services.Audio.Register("audio", () => ValueTask.FromException(cleanupFault));
        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() =>
            ConsoleSessionConstruction.CreateAsync<object>(services,
                _ => ValueTask.FromException<object>(constructionFault)).AsTask());
        Assert.Same(constructionFault, failure.InnerExceptions[0]);
        Assert.Same(cleanupFault, failure.InnerExceptions[1].InnerException);
    }

    [Fact]
    public async Task PreCancelledConstructionDoesNotInvokeTheFactory()
    {
        var services = new ConsoleSessionServices();
        bool constructed = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ConsoleSessionConstruction.CreateAsync(services, _ =>
            {
                constructed = true;
                return ValueTask.FromResult(new object());
            }, new CancellationToken(canceled: true)).AsTask());
        Assert.False(constructed);
    }
}
