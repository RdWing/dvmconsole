// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Threading;
using DvmConsole.Threading;
using UIKit;

namespace DvmConsole.iOS;

/// <summary>One bounded cleanup allowance; never a background execution loop.</summary>
internal sealed class IosBackgroundCheckpoint(
    Func<Action, nint>? begin = null, Action<nint>? end = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Func<Action, nint> begin = begin ??
        (expiration => UIApplication.SharedApplication.BeginBackgroundTask("Console checkpoint", expiration));
    private readonly Action<nint> end = end ?? UIApplication.SharedApplication.EndBackgroundTask;

    public async Task RunAsync(Func<CancellationToken, Task> checkpoint)
    {
        if (!await gate.WaitAsync(0).ConfigureAwait(false)) return;
        Task? deferred = null;
        try
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                bool ended = false;
                bool expired = false;
                nint identifier = UIApplication.BackgroundTaskInvalid;
                void EndAllowance()
                {
                    if (ended || identifier == UIApplication.BackgroundTaskInvalid) return;
                    ended = true;
                    end(identifier);
                }
                identifier = begin(() =>
                {
                    if (ended) return;
                    expired = true;
                    deadline.Cancel();
                    EndAllowance();
                });
                Task? work = null;
                try
                {
                    if (identifier == UIApplication.BackgroundTaskInvalid)
                    {
                        Console.Error.WriteLine("iOS did not grant a background checkpoint allowance.");
                        return;
                    }
                    if (expired) return;
                    work = checkpoint(deadline.Token);
                    await work.WaitAsync(deadline.Token);
                }
                catch (OperationCanceledException)
                {
                    if (work is { IsCompleted: false }) deferred = work;
                }
                catch (ObjectDisposedException) { /* Session replacement owns the retired capture. */ }
                catch (Exception exception) { Console.Error.WriteLine($"Background checkpoint failed: {exception}"); }
                finally
                {
                    EndAllowance();
                }
            });
        }
        finally
        {
            if (deferred is null) gate.Release();
            else TaskObservation.Observe(RetireAsync(deferred));
        }
    }

    private async Task RetireAsync(Task work)
    {
        try { await work.ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    internal async Task WaitForIdleAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        gate.Release();
    }
}
