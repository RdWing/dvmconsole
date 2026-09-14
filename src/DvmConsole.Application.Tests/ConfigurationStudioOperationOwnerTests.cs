// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ConfigurationStudioOperationOwnerTests
{
    [Fact]
    public async Task SaveInvalidatesCheckpointsWaitingBeforeAndDuringItsCommit()
    {
        var owner = new ConfigurationStudioOperationOwner();
        var firstEntered = Barrier(); var releaseFirst = Barrier();
        var saveEntered = Barrier(); var releaseSave = Barrier();
        Task<bool> first = owner.CheckpointAsync(async _ => { firstEntered.SetResult(); await releaseFirst.Task; });
        await firstEntered.Task;
        Task<bool> stale = owner.CheckpointAsync(_ => throw new InvalidOperationException("Stale checkpoint ran."));
        Task<int> save = owner.SaveAsync(async () => { saveEntered.SetResult(); await releaseSave.Task; return 42; });
        releaseFirst.SetResult();
        Assert.True(await first);
        Assert.False(await stale);
        await saveEntered.Task;
        Task<bool> duringSave = owner.CheckpointAsync(_ => throw new InvalidOperationException("Pre-commit editor state ran."));
        releaseSave.SetResult();
        Assert.Equal(42, await save);
        Assert.False(await duringSave);
        Assert.True(await owner.CheckpointAsync(_ => Task.CompletedTask));
    }

    [Fact]
    public async Task CloseWaitsForAcceptedCheckpointAndRetiresOnceWithoutRecreatingDraft()
    {
        var owner = new ConfigurationStudioOperationOwner();
        var entered = Barrier(); var release = Barrier();
        Task<bool> checkpoint = owner.CheckpointAsync(async _ => { entered.SetResult(); await release.Task; });
        await entered.Task;
        Task<bool> queued = owner.CheckpointAsync(_ => throw new InvalidOperationException("Retired checkpoint ran."));
        int retired = 0;
        Task close = owner.CloseAsync(() => { retired++; return Task.CompletedTask; }).AsTask();
        Assert.False(close.IsCompleted);
        Assert.False(await owner.CheckpointAsync(_ => throw new InvalidOperationException("Closed checkpoint ran.")));
        release.SetResult();
        Assert.True(await checkpoint);
        Assert.False(await queued);
        await close;
        await owner.CloseAsync(() => throw new InvalidOperationException("Retired twice."));
        Assert.Equal(1, retired);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => owner.SaveAsync(() => Task.FromResult(1)));
    }

    [Fact]
    public async Task FailedSaveReleasesOwnershipForFreshCheckpoint()
    {
        var owner = new ConfigurationStudioOperationOwner();
        await Assert.ThrowsAsync<IOException>(() => owner.SaveAsync<int>(() => throw new IOException("Full storage")));
        Assert.True(await owner.CheckpointAsync(_ => Task.CompletedTask));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.CheckpointAsync(_ => Task.CompletedTask, cancellation.Token));
    }

    private static TaskCompletionSource Barrier() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
