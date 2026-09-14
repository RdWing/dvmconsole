// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ConsoleTransmitRetirementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialConstructionRetiresUnderBothGatesAndReleasesThemAfterFailure(bool fail)
    {
        var runtime = new ConsoleTransmitRuntime();
        using var ptt = new SemaphoreSlim(1, 1);
        using var admission = new SemaphoreSlim(1, 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("Retirement fixture failure.");
        Task retirement = runtime.DisposeCoordinatorsAsync(ptt, admission, async () =>
        {
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
            if (fail) throw failure;
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, ptt.CurrentCount);
            Assert.Equal(0, admission.CurrentCount);
            Assert.False(retirement.IsCompleted);
        }
        finally { release.TrySetResult(); }

        if (fail)
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
                () => retirement.WaitAsync(TimeSpan.FromSeconds(10))));
        else
            await retirement.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, ptt.CurrentCount);
        Assert.Equal(1, admission.CurrentCount);
    }
}
