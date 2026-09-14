// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

internal sealed class P25KeyRequestCoordinator : IAsyncDisposable
{
    internal static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan RequestSpacing = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly object sync = new();
    private readonly Dictionary<string, RequestSchedule> schedules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private readonly HashSet<RequestSchedule> ownedSchedules = new();
    private readonly AsyncDisposal disposal = new();
    private bool disposed;

    public P25KeyRequestCoordinator()
        : this(SystemApplicationDelay.Instance.DelayAsync)
    {
    }

    internal P25KeyRequestCoordinator(Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        this.delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
    }

    public Task Schedule(
        string systemName,
        IReadOnlyList<(byte AlgorithmId, ushort KeyId)> requests,
        Func<bool> isConnected,
        Action<byte, ushort> requestKey,
        Func<byte, ushort, bool> hasResponse,
        Action<byte, ushort> retryKey,
        Action<Exception>? handleFailure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemName);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(isConnected);
        ArgumentNullException.ThrowIfNull(requestKey);
        ArgumentNullException.ThrowIfNull(hasResponse);
        ArgumentNullException.ThrowIfNull(retryKey);

        if (requests.Count == 0)
        {
            Cancel(systemName);
            return Task.CompletedTask;
        }

        RequestSchedule? replaced;
        RequestSchedule schedule;
        lock (sync)
        {
            if (disposed)
                return Task.CompletedTask;
            schedule = new RequestSchedule();
            ownedSchedules.Add(schedule);
            schedules.Remove(systemName, out replaced);
            schedules[systemName] = schedule;
        }

        replaced?.Cancel();
        _ = RunAsync(
            systemName,
            requests,
            isConnected,
            requestKey,
            hasResponse,
            retryKey,
            handleFailure,
            schedule);
        return schedule.Completion.Task;
    }

    public void Cancel(string systemName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemName);
        RequestSchedule? schedule;
        lock (sync)
            schedules.Remove(systemName, out schedule);
        schedule?.Cancel();
    }

    public ValueTask DisposeAsync() => disposal.RunAsync(DisposeCoreAsync);

    private async Task DisposeCoreAsync()
    {
        RequestSchedule[] active;
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            active = ownedSchedules.ToArray();
            schedules.Clear();
        }

        foreach (RequestSchedule schedule in active)
            schedule.Cancel();
        await Task.WhenAll(active.Select(schedule => schedule.Completion.Task)).ConfigureAwait(false);
    }

    private async Task RunAsync(
        string systemName,
        IReadOnlyList<(byte AlgorithmId, ushort KeyId)> requests,
        Func<bool> isConnected,
        Action<byte, ushort> requestKey,
        Func<byte, ushort, bool> hasResponse,
        Action<byte, ushort> retryKey,
        Action<Exception>? handleFailure,
        RequestSchedule schedule)
    {
        CancellationToken cancellationToken = schedule.Token;
        Exception? failure = null;
        try
        {
            await delayAsync(StartupDelay, cancellationToken).ConfigureAwait(false);
            if (!await SendPassAsync(
                    requests,
                    isConnected,
                    requestKey,
                    handleFailure,
                    cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await delayAsync(RetryDelay, cancellationToken).ConfigureAwait(false);
            (byte AlgorithmId, ushort KeyId)[] unanswered = requests
                .Where(request => !hasResponse(request.AlgorithmId, request.KeyId))
                .ToArray();
            await SendPassAsync(
                    unanswered,
                    isConnected,
                    retryKey,
                    handleFailure,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Connection lifecycle cancellation is expected.
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            schedule.Finish(() => CompleteSchedule(systemName, schedule, failure));
        }
    }

    private void CompleteSchedule(string systemName, RequestSchedule schedule, Exception? failure)
    {
        lock (sync)
        {
            if (schedules.TryGetValue(systemName, out RequestSchedule? current) &&
                ReferenceEquals(current, schedule))
            {
                schedules.Remove(systemName);
            }
            ownedSchedules.Remove(schedule);
            if (failure is null)
                schedule.Completion.TrySetResult();
            else
                schedule.Completion.TrySetException(failure);
        }
    }

    private async Task<bool> SendPassAsync(
        IReadOnlyList<(byte AlgorithmId, ushort KeyId)> requests,
        Func<bool> isConnected,
        Action<byte, ushort> requestKey,
        Action<Exception>? handleFailure,
        CancellationToken cancellationToken)
    {
        for (int index = 0; index < requests.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!isConnected())
                return false;

            (byte algorithmId, ushort keyId) = requests[index];
            try
            {
                requestKey(algorithmId, keyId);
            }
            catch (Exception exception)
            {
                handleFailure?.Invoke(exception);
            }

            if (index < requests.Count - 1)
                await delayAsync(RequestSpacing, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private sealed class RequestSchedule
    {
        private readonly object sync = new();
        private readonly CancellationTokenSource cancellation = new();
        private Action? complete;
        private bool finished;
        private int cancelling;

        public CancellationToken Token { get; }
        public TaskCompletionSource Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public RequestSchedule() => Token = cancellation.Token;

        public void Cancel()
        {
            lock (sync)
            {
                if (finished)
                    return;
                cancelling++;
            }
            try
            {
                cancellation.Cancel();
            }
            finally
            {
                lock (sync)
                {
                    cancelling--;
                    CompleteIfReady();
                }
            }
        }

        public void Finish(Action onCompleted)
        {
            lock (sync)
            {
                finished = true;
                complete = onCompleted;
                CompleteIfReady();
            }
        }

        private void CompleteIfReady()
        {
            // Cancellation callbacks can finish the worker synchronously. Keep its
            // source alive until all Cancel calls have returned before retiring it.
            if (!finished || cancelling != 0 || complete is null)
                return;
            cancellation.Dispose();
            Action onCompleted = complete;
            complete = null;
            onCompleted();
        }
    }
}
