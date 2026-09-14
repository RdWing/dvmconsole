// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>One active connection cue and at most one latest pending cue, with no replay after cancellation.</summary>
internal sealed class ConnectionCueWorker : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly ConnectionChimeTracker edges = new();
    private readonly Func<bool> canPlay;
    private readonly Func<bool, CancellationToken, Task> play;
    private readonly SingleFlightAsyncAction worker;
    private CancellationTokenSource? active;
    private bool? pending;
    private bool disposed;

    public ConnectionCueWorker(Func<bool> canPlay, Func<bool, CancellationToken, Task> play, Action<Exception> reportFailure)
    {
        this.canPlay = canPlay;
        this.play = play;
        worker = new(PlayPendingAsync, reportFailure);
    }

    public void Observe(string system, RadioConnectionState state)
    {
        lock (sync)
        {
            if (disposed || !edges.ShouldPlay(system, state) || !canPlay()) return;
            pending = state == RadioConnectionState.Connected;
            worker.Request();
        }
    }

    public void Cancel()
    {
        lock (sync)
        {
            pending = null;
            active?.Cancel();
        }
    }

    private async Task PlayPendingAsync(CancellationToken token)
    {
        bool connected;
        CancellationTokenSource playback;
        lock (sync)
        {
            if (disposed || pending is not { } cue || !canPlay()) { pending = null; return; }
            connected = cue;
            pending = null;
            active = playback = CancellationTokenSource.CreateLinkedTokenSource(token);
        }
        try { await play(connected, playback.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (playback.IsCancellationRequested) { }
        finally
        {
            lock (sync) { active = null; playback.Dispose(); }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (sync) { disposed = true; Cancel(); }
        return worker.DisposeAsync();
    }
}
