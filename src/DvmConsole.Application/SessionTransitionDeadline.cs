// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Threading;

namespace DvmConsole.Application;

public sealed class SessionTransitionDeadline : IDisposable
{
    private readonly TimeSpan timeout;
    private readonly long started;
    private readonly TimeProvider timeProvider;
    private readonly CancellationTokenSource deadline;

    public SessionTransitionDeadline(TimeSpan timeout, TimeProvider? timeProvider = null)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        this.timeout = timeout;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        started = this.timeProvider.GetTimestamp();
        deadline = new CancellationTokenSource(timeout, this.timeProvider);
    }

    public async Task RunAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken callerCancellation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var stepCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token,
            callerCancellation);
        CancellationToken stepToken = stepCancellation.Token;
        // Start every independent cleanup owner even when an earlier phase has
        // exhausted the wait budget. A closed deadline then abandons and
        // observes that work instead of silently skipping its safety actions.
        Task task = Task.Run(
            async () => await operation(stepToken).ConfigureAwait(false),
            CancellationToken.None);
        try
        {
            await task.WaitAsync(stepToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            deadline.IsCancellationRequested && !callerCancellation.IsCancellationRequested)
        {
            TaskObservation.Observe(task);
            throw new TimeoutException("The session transition exceeded its overall deadline.", exception);
        }
        catch
        {
            if (!task.IsCompleted)
                TaskObservation.Observe(task);
            throw;
        }
    }

    public async Task WaitAsync(
        SemaphoreSlim gate,
        CancellationToken callerCancellation)
    {
        ArgumentNullException.ThrowIfNull(gate);
        TimeSpan remaining = timeout - timeProvider.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero)
            throw new TimeoutException("The session transition exceeded its overall deadline.");

        using var stepCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token,
            callerCancellation);
        try
        {
            await gate.WaitAsync(stepCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            deadline.IsCancellationRequested && !callerCancellation.IsCancellationRequested)
        {
            throw new TimeoutException("The session transition exceeded its overall deadline.", exception);
        }
    }

    public void Dispose() => deadline.Dispose();
}
