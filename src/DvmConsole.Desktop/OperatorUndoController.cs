// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Desktop;

/// <summary>
/// Owns the single operator-visible undo window. Beginning a new action commits
/// the previous pending action, so delayed resource cleanup has one explicit
/// owner and can never be left waiting indefinitely.
/// </summary>
internal sealed class OperatorUndoController : IAsyncDisposable
{
    internal static readonly TimeSpan DefaultUndoWindow = TimeSpan.FromSeconds(8);

    private readonly object sync = new();
    private readonly TimeSpan undoWindow;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private readonly Action<Action> dispatch;
    private readonly Action<Exception> reportFailure;
    private PendingAction? pending;
    private CancellationTokenSource? expiryCancellation;
    private long nextId;
    private bool disposed;

    public OperatorUndoController(
        Action<Action> dispatch,
        Action<Exception> reportFailure,
        TimeSpan? undoWindow = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        this.dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        this.reportFailure = reportFailure ?? throw new ArgumentNullException(nameof(reportFailure));
        this.undoWindow = undoWindow ?? DefaultUndoWindow;
        if (this.undoWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(undoWindow));
        this.delayAsync = delayAsync ?? Task.Delay;
    }

    public event EventHandler? Changed;

    public bool CanUndo
    {
        get
        {
            lock (sync)
                return pending is not null;
        }
    }

    public string Message
    {
        get
        {
            lock (sync)
                return pending?.Message ?? string.Empty;
        }
    }

    public void Begin(
        string message,
        Func<ValueTask> undo,
        Func<ValueTask>? commit = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(undo);

        PendingAction? previous;
        CancellationTokenSource expiry;
        PendingAction current;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            previous = pending;
            expiryCancellation?.Cancel();
            expiryCancellation?.Dispose();
            expiry = new CancellationTokenSource();
            expiryCancellation = expiry;
            current = new PendingAction(
                ++nextId,
                message,
                undo,
                commit ?? (static () => ValueTask.CompletedTask));
            pending = current;
        }

        PublishChanged();
        if (previous is not null)
            Observe(CommitAsync(previous));
        Observe(ExpireAsync(current.Id, expiry));
    }

    public async ValueTask<bool> UndoAsync()
    {
        PendingAction? action = TakePending();
        if (action is null)
            return false;

        PublishChanged();
        try
        {
            await action.Undo().ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            Report(exception);
            await CommitAsync(action).ConfigureAwait(false);
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        PendingAction? action;
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            action = pending;
            pending = null;
            expiryCancellation?.Cancel();
            expiryCancellation?.Dispose();
            expiryCancellation = null;
        }

        if (action is not null)
            await CommitAsync(action).ConfigureAwait(false);
    }

    private PendingAction? TakePending(long? expectedId = null)
    {
        lock (sync)
        {
            if (pending is null || (expectedId.HasValue && pending.Id != expectedId.Value))
                return null;

            PendingAction action = pending;
            pending = null;
            expiryCancellation?.Cancel();
            expiryCancellation?.Dispose();
            expiryCancellation = null;
            return action;
        }
    }

    private async Task ExpireAsync(long actionId, CancellationTokenSource expiry)
    {
        try
        {
            await delayAsync(undoWindow, expiry.Token).ConfigureAwait(false);
            dispatch(() => ExpireOnDispatcher(actionId));
        }
        catch (OperationCanceledException) when (expiry.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Report(exception);
        }
    }

    private void ExpireOnDispatcher(long actionId)
    {
        PendingAction? action = TakePending(actionId);
        if (action is null)
            return;
        PublishChanged();
        Observe(CommitAsync(action));
    }

    private async Task CommitAsync(PendingAction action)
    {
        try
        {
            await action.Commit().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Report(exception);
        }
    }

    private void Observe(Task task)
        => _ = ObserveAsync(task);

    private async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Report(exception);
        }
    }

    private void PublishChanged()
    {
        Delegate[] observers = Changed?.GetInvocationList() ?? [];
        foreach (Delegate observer in observers)
        {
            try
            {
                ((EventHandler)observer)(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                Report(exception);
            }
        }
    }

    private void Report(Exception exception)
    {
        try
        {
            reportFailure(exception);
        }
        catch
        {
            // Reporting cannot destabilize the undo or cleanup lifetime.
        }
    }

    private sealed record PendingAction(
        long Id,
        string Message,
        Func<ValueTask> Undo,
        Func<ValueTask> Commit);
}
