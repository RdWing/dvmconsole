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
            schedule = new RequestSchedule(new CancellationTokenSource());
            schedules.Remove(systemName, out replaced);
            schedules[systemName] = schedule;
        }

        replaced?.Cancellation.Cancel();
        schedule.Task = RunAsync(
            systemName,
            requests,
            isConnected,
            requestKey,
            hasResponse,
            retryKey,
            handleFailure,
            schedule);
        return schedule.Task;
    }

    public void Cancel(string systemName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemName);
        RequestSchedule? schedule;
        lock (sync)
            schedules.Remove(systemName, out schedule);
        schedule?.Cancellation.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        RequestSchedule[] active;
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            active = schedules.Values.ToArray();
            schedules.Clear();
        }

        foreach (RequestSchedule schedule in active)
            schedule.Cancellation.Cancel();
        await Task.WhenAll(active.Select(schedule => schedule.Task)).ConfigureAwait(false);
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
        CancellationToken cancellationToken = schedule.Cancellation.Token;
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
        finally
        {
            lock (sync)
            {
                if (schedules.TryGetValue(systemName, out RequestSchedule? current) &&
                    ReferenceEquals(current, schedule))
                {
                    schedules.Remove(systemName);
                }
            }
            schedule.Cancellation.Dispose();
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

    private sealed class RequestSchedule(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Task { get; set; } = Task.CompletedTask;
    }
}
