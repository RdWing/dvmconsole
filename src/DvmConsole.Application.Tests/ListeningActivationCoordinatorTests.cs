// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ListeningActivationCoordinatorTests
{
    [Fact]
    public async Task ResumeRetriesFailedActivationBeforeOrdinaryRecovery()
    {
        int attempts = 0, recoveries = 0;
        await using var owner = new ListeningActivationCoordinator(_ =>
        {
            if (++attempts == 1) throw new IOException("Initial output unavailable");
            return Task.CompletedTask;
        }, _ => { recoveries++; return Task.CompletedTask; });
        await Assert.ThrowsAsync<IOException>(() => owner.ActivateAsync());
        await owner.ResumeAsync();
        Assert.Equal(2, attempts);
        Assert.Equal(0, recoveries);
        await owner.ActivateAsync();
        await owner.ResumeAsync();
        Assert.Equal(2, attempts);
        Assert.Equal(1, recoveries);
    }

    [Fact]
    public async Task ConcurrentActivationHasOneOwner()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int starts = 0;
        await using var owner = new ListeningActivationCoordinator(_ => { starts++; return release.Task; });
        Task first = owner.ActivateAsync(), second = owner.ActivateAsync();
        Assert.Equal(1, starts);
        Assert.False(second.IsCompleted);
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task RetirementCancelsQueuedRecoveryAndRejectsLateActivation()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken activationToken = default;
        var owner = new ListeningActivationCoordinator(token => { activationToken = token; return release.Task; });
        Task first = owner.ActivateAsync(), queued = owner.ResumeAsync();
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = activationToken.Register(() => canceled.TrySetResult());
        Task retired = owner.DisposeAsync().AsTask();
        try
        {
            // CancelAsync schedules the linked token callbacks; observe delivery
            // before asserting retirement, rather than assuming it is inline.
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(activationToken.IsCancellationRequested);
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            await retired.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => owner.ResumeAsync());
        }
        finally
        {
            release.TrySetResult();
            await retired.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
