// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>
/// Owns the single operator-visible undo window. Beginning a new action commits
/// the previous pending action, so delayed resource cleanup has one explicit
/// owner and can never be left waiting indefinitely.
/// </summary>
public sealed class OperatorUndoController : IAsyncDisposable
{
    internal static readonly TimeSpan DefaultUndoWindow = TimeSpan.FromSeconds(8);

    private readonly object sync = new();
    private readonly TimeSpan undoWindow;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private readonly Action<Action> dispatch;
    private readonly Action<Exception> reportFailure;
    private readonly AsyncDisposal disposal = new();
    private readonly HashSet<TaskCompletionSource> operations = [];
    private PendingAction? pending;
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
        PendingAction current;
        TaskCompletionSource expiry;
        TaskCompletionSource? previousCommit;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            previous = pending;
            previous?.Retired.TrySetResult();
            current = new PendingAction(
                ++nextId,
                message,
                undo,
                commit ?? (static () => ValueTask.CompletedTask));
            pending = current;
            expiry = RegisterOperation();
            previousCommit = previous is null ? null : RegisterOperation();
        }

        Observe(ExpireAsync(current), expiry);
        if (previous is not null)
            Observe(CommitAsync(previous), previousCommit!);
        PublishChanged();
    }

    public async ValueTask<bool> UndoAsync()
    {
        PendingAction? action;
        TaskCompletionSource operation;
        lock (sync)
        {
            action = TakePending();
            if (action is null) return false;
            operation = RegisterOperation();
        }

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
        finally { CompleteOperation(operation); }
    }

    public ValueTask DisposeAsync() => disposal.RunAsync(DisposeCoreAsync);

    private async Task DisposeCoreAsync()
    {
        PendingAction? action;
        TaskCompletionSource? commit;
        Task[] retiring;
        lock (sync)
        {
            action = TakePending();
            disposed = true;
            commit = action is null ? null : RegisterOperation();
            retiring = operations.Select(operation => operation.Task).ToArray();
        }

        if (action is not null)
            Observe(CommitAsync(action), commit!);
        await Task.WhenAll(retiring).ConfigureAwait(false);
    }

    private PendingAction? TakePending(long? expectedId = null)
    {
        lock (sync)
        {
            if (disposed || pending is null || (expectedId.HasValue && pending.Id != expectedId.Value))
                return null;

            PendingAction action = pending;
            pending = null;
            action.Retired.TrySetResult();
            return action;
        }
    }

    private async Task ExpireAsync(PendingAction action)
    {
        // The worker alone owns cancellation and disposal of its timer. Retirement
        // signals never race a disposed CTS or execute injected callbacks under sync.
        using var expiry = new CancellationTokenSource();
        try
        {
            Task delay = delayAsync(undoWindow, expiry.Token);
            await Task.WhenAny(delay, action.Retired.Task).ConfigureAwait(false);
            if (action.Retired.Task.IsCompleted)
                await expiry.CancelAsync().ConfigureAwait(false);
            await delay.ConfigureAwait(false);
            if (!action.Retired.Task.IsCompleted)
                dispatch(() => ExpireOnDispatcher(action.Id));
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
        PendingAction? action;
        TaskCompletionSource operation;
        lock (sync)
        {
            action = TakePending(actionId);
            if (action is null) return;
            operation = RegisterOperation();
        }
        PublishChanged();
        Observe(CommitAsync(action), operation);
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

    // Register while holding sync, before invoking callbacks or yielding. Disposal
    // then sees every admitted restoration, expiry worker and deferred commit.
    private TaskCompletionSource RegisterOperation()
    {
        var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        operations.Add(operation);
        return operation;
    }

    private void CompleteOperation(TaskCompletionSource operation)
    {
        lock (sync)
        {
            operations.Remove(operation);
            operation.TrySetResult();
        }
    }

    private void Observe(Task task, TaskCompletionSource operation)
        => _ = ObserveAsync(task, operation);

    private async Task ObserveAsync(Task task, TaskCompletionSource operation)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Report(exception);
        }
        finally { CompleteOperation(operation); }
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
        Func<ValueTask> Commit)
    {
        public TaskCompletionSource Retired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
