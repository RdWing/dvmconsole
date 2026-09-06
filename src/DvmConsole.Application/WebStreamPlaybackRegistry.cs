// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;

namespace DvmConsole.Application;

/// <summary>
/// Owns the active and preparing stream identities independently from HTTP,
/// decoding, and playback mechanics. The coordinator still serializes
/// multi-step transitions; this registry makes synchronous status reads safe.
/// </summary>
internal sealed class WebStreamPlaybackRegistry
{
    private readonly object sync = new();
    private readonly Dictionary<WebStreamId, WebStreamPlaybackSession> sessions = [];
    private readonly Dictionary<WebStreamId, WebStreamPendingStart> pendingStarts = [];

    public IReadOnlyList<WebStreamId> ActiveStreamIds
    {
        get
        {
            lock (sync)
                return sessions.Keys.ToArray();
        }
    }

    public int SessionCount
    {
        get
        {
            lock (sync)
                return sessions.Count;
        }
    }

    public int PendingCount
    {
        get
        {
            lock (sync)
                return pendingStarts.Count;
        }
    }

    public bool IsActive(WebStreamId streamId)
    {
        lock (sync)
            return sessions.ContainsKey(streamId);
    }

    public void SetVolume(WebStreamId streamId, double volume)
    {
        lock (sync)
        {
            if (sessions.TryGetValue(streamId, out WebStreamPlaybackSession? session))
                session.SetVolume(volume);
        }
    }

    public bool ContainsOrPending(WebStreamId streamId)
    {
        lock (sync)
            return sessions.ContainsKey(streamId) || pendingStarts.ContainsKey(streamId);
    }

    public void AddPending(WebStreamId streamId, WebStreamPendingStart pending)
    {
        lock (sync)
            pendingStarts.Add(streamId, pending);
    }

    public bool IsCurrentPending(WebStreamId streamId, WebStreamPendingStart pending)
    {
        lock (sync)
        {
            return pendingStarts.TryGetValue(streamId, out WebStreamPendingStart? current) &&
                   ReferenceEquals(current, pending);
        }
    }

    public bool TryGetPending(WebStreamId streamId, out WebStreamPendingStart? pending)
    {
        lock (sync)
            return pendingStarts.TryGetValue(streamId, out pending);
    }

    public bool RemovePending(WebStreamId streamId, WebStreamPendingStart pending)
    {
        lock (sync)
        {
            if (!pendingStarts.TryGetValue(streamId, out WebStreamPendingStart? current) ||
                !ReferenceEquals(current, pending))
            {
                return false;
            }
            return pendingStarts.Remove(streamId);
        }
    }

    public void AddSession(WebStreamId streamId, WebStreamPlaybackSession session)
    {
        lock (sync)
            sessions.Add(streamId, session);
    }

    public bool TryRemoveSession(
        WebStreamId streamId,
        out WebStreamPlaybackSession? session)
    {
        lock (sync)
            return sessions.Remove(streamId, out session);
    }

    public void RemoveSession(WebStreamId streamId, WebStreamPlaybackSession session)
    {
        lock (sync)
        {
            if (sessions.TryGetValue(streamId, out WebStreamPlaybackSession? current) &&
                ReferenceEquals(current, session))
            {
                sessions.Remove(streamId);
            }
        }
    }

    public WebStreamPendingStart[] TakeAllPending()
    {
        lock (sync)
        {
            WebStreamPendingStart[] result = pendingStarts.Values.ToArray();
            pendingStarts.Clear();
            return result;
        }
    }

    public WebStreamPlaybackSession[] TakeAllSessions()
    {
        lock (sync)
        {
            WebStreamPlaybackSession[] result = sessions.Values.ToArray();
            sessions.Clear();
            return result;
        }
    }
}

internal sealed class WebStreamPlaybackSession(
    IAudioPcmStreamReader reader,
    IAudioPlayback playback,
    PcmRateConverter? rateConverter)
{
    private readonly AsyncDisposal resourceDisposal = new();

    public IAudioPcmStreamReader Reader { get; } = reader;
    public IAudioPlayback Playback { get; } = playback;
    public PcmRateConverter? RateConverter { get; } = rateConverter;
    public CancellationTokenSource Cancellation { get; } = new();
    public Task? RunTask { get; set; }

    public void SetVolume(double volume)
    {
        if (Playback is not IAudioGainControl gainControl)
        {
            throw new NotSupportedException(
                "The web-stream playback route does not support independent volume.");
        }
        gainControl.Gain = volume;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Natural completion can win the race with an operator stop.
        }

        // Reader disposal is the contract-level escape hatch for a decoder
        // that does not observe cancellation promptly. Begin it before
        // awaiting the pump so Stop and shutdown cannot deadlock behind the
        // in-flight read.
        Task resourceTask = DisposeResourcesAsync().AsTask();
        Task? runTask = RunTask;
        Task completion = runTask is null
            ? resourceTask
            : Task.WhenAll(runTask, resourceTask);
        await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeResourcesAsync()
        => resourceDisposal.RunAsync(DisposeResourcesCoreAsync);

    private async Task DisposeResourcesCoreAsync()
    {
        try
        {
            await Playback.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await Reader.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                Cancellation.Dispose();
            }
        }
    }
}

internal sealed class WebStreamPendingStart(CancellationToken cancellationToken) : IDisposable
{
    private readonly CancellationTokenSource cancellation =
        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    private readonly TaskCompletionSource completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CancellationToken Token => cancellation.Token;
    public bool IsCancellationRequested => cancellation.IsCancellationRequested;
    public Task Completion => completion.Task;

    public void Cancel()
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Complete() => completion.TrySetResult();

    public void Dispose() => cancellation.Dispose();
}
