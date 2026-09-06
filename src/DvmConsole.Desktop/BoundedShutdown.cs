// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Desktop;

using System.Collections.Concurrent;
using System.Diagnostics;

internal sealed record ShutdownPhase(
    string Name,
    TimeSpan Timeout,
    Func<CancellationToken, Task> Execute);

/// <summary>
/// Runs every shutdown phase under one deadline. A failed or uncooperative
/// owner cannot prevent later owners or the final output-suppression fence from
/// being attempted.
/// </summary>
internal static class BoundedShutdown
{
    private static readonly TimeSpan SafetyFenceReserve = TimeSpan.FromMilliseconds(250);

    public static async Task RunAsync(
        IReadOnlyList<ShutdownPhase> phases,
        TimeSpan timeout,
        Action safetyFence,
        Action<string, Task>? registerAbandonedWork = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(phases);
        ArgumentNullException.ThrowIfNull(safetyFence);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var failures = new List<Exception>();
        long started = Stopwatch.GetTimestamp();

        foreach (ShutdownPhase phase in phases)
        {
            if (string.IsNullOrWhiteSpace(phase.Name))
            {
                failures.Add(new ArgumentException("Shutdown phases require a name."));
                continue;
            }
            if (phase.Timeout <= TimeSpan.Zero)
            {
                failures.Add(new ArgumentOutOfRangeException(
                    nameof(phases),
                    $"Shutdown phase '{phase.Name}' requires a positive timeout."));
                continue;
            }

            TimeSpan remaining = Remaining(timeout, started) - SafetyFenceReserve;
            using var phaseCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            if (remaining > TimeSpan.Zero)
                phaseCancellation.CancelAfter(Min(phase.Timeout, remaining));
            else
                phaseCancellation.Cancel();

            // A queued operation may start after its budget has elapsed and
            // this source has been disposed. Capture the value while owned.
            CancellationToken phaseToken = phaseCancellation.Token;
            Task operation = Task.Run(
                async () => await phase.Execute(phaseToken).ConfigureAwait(false),
                CancellationToken.None);
            try
            {
                await operation.WaitAsync(phaseCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (phaseCancellation.IsCancellationRequested)
            {
                RegisterAbandonedWork(
                    phase.Name,
                    operation,
                    registerAbandonedWork,
                    failures);
                failures.Add(new TimeoutException(
                    $"Shutdown phase '{phase.Name}' did not finish within its deadline.",
                    exception));
            }
            catch (Exception exception)
            {
                failures.Add(new InvalidOperationException(
                    $"Shutdown phase '{phase.Name}' failed.",
                    exception));
            }
        }

        try
        {
            // This contract is intentionally synchronous and nonblocking. It
            // is the last line of defence after timed cleanup: close sockets,
            // capture handles, and playback routes before exit can continue.
            safetyFence();
        }
        catch (Exception exception)
        {
            failures.Add(new InvalidOperationException(
                "The final shutdown safety fence failed.",
                exception));
        }

        if (failures.Count > 0)
            throw new AggregateException("Application shutdown completed with failures.", failures);
    }

    private static TimeSpan Remaining(TimeSpan timeout, long started)
    {
        TimeSpan remaining = timeout - Stopwatch.GetElapsedTime(started);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static TimeSpan Min(TimeSpan first, TimeSpan second)
        => first <= second ? first : second;

    private static void RegisterAbandonedWork(
        string phaseName,
        Task operation,
        Action<string, Task>? registerAbandonedWork,
        List<Exception> failures)
    {
        if (registerAbandonedWork is null)
        {
            TaskObservation.Observe(operation);
            return;
        }

        try
        {
            registerAbandonedWork(phaseName, operation);
        }
        catch (Exception exception)
        {
            TaskObservation.Observe(operation);
            failures.Add(new InvalidOperationException(
                $"Shutdown phase '{phaseName}' could not be registered as abandoned work.",
                exception));
        }
    }
}

/// <summary>
/// Retains timed-out shutdown operations until they settle. The registry is
/// diagnostic ownership only: callers never await it during application exit.
/// </summary>
internal sealed class ShutdownBackgroundWorkRegistry(
    Action<string, Exception>? faultObserver = null)
{
    private readonly ConcurrentDictionary<long, ShutdownBackgroundWork> work = [];
    private long nextId;

    public int Count => work.Count;

    public void Register(string phaseName, Task operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phaseName);
        ArgumentNullException.ThrowIfNull(operation);
        long id = Interlocked.Increment(ref nextId);
        work[id] = new ShutdownBackgroundWork(phaseName, operation);
        TaskObservation.Observe(ObserveAsync(id, phaseName, operation));
    }

    private async Task ObserveAsync(long id, string phaseName, Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A timed-out phase normally settles through cancellation.
        }
        catch (Exception exception)
        {
            try
            {
                faultObserver?.Invoke(phaseName, exception);
            }
            catch
            {
                // Failure reporting cannot make the abandoned task unobserved.
            }
        }
        finally
        {
            work.TryRemove(id, out _);
        }
    }

    private sealed record ShutdownBackgroundWork(string PhaseName, Task Operation);
}
