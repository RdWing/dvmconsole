// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>
/// Collapses pending slider intent per channel and setting. Each caller joins
/// the durable write that supersedes its value; failed writes remain observable.
/// The session supplies command admission and owns cancellation of active work.
/// </summary>
internal sealed class ChannelAudioCommandQueue(
    Func<Func<CancellationToken, Task>, CancellationToken, ValueTask> run,
    Func<ChannelAudioSettingsController> settings,
    TimeProvider time, IApplicationDelay delay)
{
    private readonly object sync = new();
    private readonly Dictionary<(ChannelId Id, bool Balance), List<Request>> pending = [];
    private readonly HashSet<Task> workers = [];
    private readonly Dictionary<(ChannelId Id, bool Balance), long> lastWrite = [];
    private static readonly TimeSpan MinimumWriteInterval = TimeSpan.FromMilliseconds(40);

    public ValueTask SetAsync(ChannelId id, double value, bool balance, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var request = new Request(value, token);
        var key = (id, balance);
        lock (sync)
        {
            if (pending.TryGetValue(key, out var requests)) requests.Add(request);
            else
            {
                pending.Add(key, [request]);
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                workers.Add(completion.Task);
                _ = ProcessAsync(key, completion);
            }
        }
        return new ValueTask(request.Completion.Task);
    }

    public async Task FlushAsync(CancellationToken token)
    {
        Task[] active;
        lock (sync) active = workers.ToArray();
        await Task.WhenAll(active).WaitAsync(token).ConfigureAwait(false);
    }

    private async Task ProcessAsync((ChannelId Id, bool Balance) key, TaskCompletionSource completion)
    {
        try
        {
            while (true)
            {
                List<Request>? batch = null;
                try
                {
                    TimeSpan remaining;
                    lock (sync)
                        remaining = lastWrite.TryGetValue(key, out long started)
                            ? MinimumWriteInterval - time.GetElapsedTime(started) : TimeSpan.Zero;
                    // Limit writes even on fast storage. The delay is outside the
                    // session command gate, so unrelated commands remain admitted.
                    if (remaining > TimeSpan.Zero) await delay.DelayAsync(remaining).ConfigureAwait(false);
                    await run(async sessionToken =>
                    {
                        lock (sync)
                        {
                            batch = pending[key];
                            pending[key] = [];
                            lastWrite[key] = time.GetTimestamp();
                        }
                        Request? latest = batch.LastOrDefault(request => !request.Token.IsCancellationRequested);
                        if (latest is null) return;
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(sessionToken, latest.Token);
                        if (key.Balance) await settings().SetBalanceAsync(key.Id, latest.Value, linked.Token).ConfigureAwait(false);
                        else await settings().SetGainAsync(key.Id, latest.Value, linked.Token).ConfigureAwait(false);
                    }, CancellationToken.None).ConfigureAwait(false);
                    Complete(batch, null);
                }
                catch (Exception failure)
                {
                    // Admission can fail before the command delegate takes its batch.
                    if (batch is null)
                        lock (sync) { batch = pending[key]; pending[key] = []; }
                    Complete(batch, failure);
                }
                lock (sync)
                {
                    if (pending[key].Count != 0) continue;
                    pending.Remove(key);
                    workers.Remove(completion.Task);
                    completion.TrySetResult();
                    return;
                }
            }
        }
        catch (Exception failure)
        {
            lock (sync)
            {
                if (pending.Remove(key, out var requests)) Complete(requests, failure);
                workers.Remove(completion.Task);
            }
            completion.TrySetException(failure);
        }
    }

    private static void Complete(List<Request>? requests, Exception? failure)
    {
        if (requests is null) return;
        foreach (Request request in requests)
        {
            if (request.Token.IsCancellationRequested) request.Completion.TrySetCanceled(request.Token);
            else if (failure is not null) request.Completion.TrySetException(failure);
            else request.Completion.TrySetResult();
        }
    }

    private sealed record Request(double Value, CancellationToken Token)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
