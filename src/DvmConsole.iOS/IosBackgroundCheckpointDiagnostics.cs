// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Threading;

namespace DvmConsole.iOS;

internal static class IosBackgroundCheckpointDiagnostics
{
    public static async Task RunAsync()
    {
        Action? expire = null;
        int ended = 0;
        int duplicates = 0;
        var owner = new IosBackgroundCheckpoint(
            callback => { expire = callback; return 42; }, identifier =>
            {
                if (identifier != 42) throw new InvalidOperationException("Wrong background allowance ended.");
                ended++;
            });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken intent = default;
        Task pending = owner.RunAsync(async token =>
        {
            intent = token;
            entered.SetResult();
            await finish.Task; // Model native I/O which cannot honor cancellation immediately.
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                expire!();
                if (ended != 1 || !intent.IsCancellationRequested)
                    throw new InvalidOperationException("Expiration did not synchronously end and cancel the allowance.");
            });
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
            await owner.RunAsync(_ => { duplicates++; return Task.CompletedTask; });
            if (duplicates != 0 || finish.Task.IsCompleted)
                throw new InvalidOperationException("Expired checkpoint lost ownership of unfinished I/O.");
        }
        finally { finish.TrySetResult(); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await owner.WaitForIdleAsync(deadline.Token);
        await owner.RunAsync(_ => Task.CompletedTask);
        if (ended != 2) throw new InvalidOperationException("A completed checkpoint did not release its allowance exactly once.");
    }
}
