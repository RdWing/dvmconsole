// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;

namespace DvmConsole.Presentation;

// Renderer-neutral PTT intent reconciler shared by Cards and List. Suspension,
// shutdown, and renderer replacement converge on ReleaseAllAsync, which is safe
// to call more than once and while a slow transmitter is still starting.
public sealed class ChannelPttController : IAsyncDisposable
{
    private enum LifecycleState
    {
        Idle,
        Starting,
        Active,
        StopPending,
        FailedStop
    }

    private sealed class ChannelState
    {
        public bool Held { get; set; }
        public bool Latched { get; set; }
        public LifecycleState Lifecycle { get; set; }
        public bool Requested => Held || Latched;
    }

    private readonly Func<ChannelId, CancellationToken, ValueTask<bool>> start;
    private readonly Func<ChannelId, CancellationToken, ValueTask> stop;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private readonly Dictionary<ChannelId, ChannelState> channels = [];
    private Task? disposeTask;
    private bool disposed;

    public ChannelPttController(
        Func<ChannelId, CancellationToken, ValueTask<bool>> start,
        Func<ChannelId, CancellationToken, ValueTask> stop)
    {
        this.start = start ?? throw new ArgumentNullException(nameof(start));
        this.stop = stop ?? throw new ArgumentNullException(nameof(stop));
    }

    public async ValueTask PressAsync(
        ChannelId channelId,
        CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            if (disposed)
                return;
            ChannelState state = GetOrCreateState(channelId);
            if (state.Held)
                return;
            state.Held = true;
        }
        await ReconcileAsync(channelId, cancellationToken);
    }

    public async ValueTask ReleaseAsync(
        ChannelId channelId,
        CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            if (disposed || !channels.TryGetValue(channelId, out ChannelState? state) || !state.Held)
                return;
            state.Held = false;
        }
        await ReconcileAsync(channelId, cancellationToken);
    }

    public async ValueTask ToggleAsync(
        ChannelId channelId,
        CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            if (disposed)
                return;
            ChannelState state = GetOrCreateState(channelId);
            state.Latched = !state.Latched;
        }
        await ReconcileAsync(channelId, cancellationToken);
    }

    // A renderer can observe an active transmission that was started by a
    // different PTT source (for example, a keyboard binding).  In that case a
    // visible Release control must be authoritative even though this
    // controller has no matching held/latched entry.
    public async ValueTask UnkeyAsync(
        ChannelId channelId,
        CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            if (disposed)
                return;
            ChannelState state = GetOrCreateState(channelId);
            state.Held = false;
            state.Latched = false;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            await StopKnownOrExternalAsync(channelId, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask ReleaseAllAsync(CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            foreach (ChannelState state in channels.Values)
            {
                state.Held = false;
                state.Latched = false;
            }
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            ChannelId[] activeChannels;
            lock (sync)
                activeChannels = channels
                    .Where(pair => pair.Value.Lifecycle != LifecycleState.Idle)
                    .Select(pair => pair.Key)
                    .ToArray();

            var failures = new List<Exception>();
            foreach (ChannelId channelId in activeChannels)
            {
                try
                {
                    await StopKnownOrExternalAsync(channelId, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    failures.Add(new InvalidOperationException(
                        $"PTT release failed for channel '{channelId}'.",
                        exception));
                }
            }
            if (failures.Count == 1)
                throw failures[0].InnerException ?? failures[0];
            if (failures.Count > 1)
                throw new AggregateException("One or more PTT releases failed.", failures);
        }
        finally
        {
            gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            disposeTask ??= DisposeCoreAsync();
            return new ValueTask(disposeTask);
        }
    }

    private async ValueTask ReconcileAsync(ChannelId channelId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            bool shouldStart;
            lock (sync)
            {
                if (disposed)
                    return;
                ChannelState state = GetOrCreateState(channelId);
                if (!state.Requested)
                {
                    shouldStart = false;
                }
                else if (state.Lifecycle == LifecycleState.Idle)
                {
                    state.Lifecycle = LifecycleState.Starting;
                    shouldStart = true;
                }
                else
                {
                    // Active and failed-stop both still represent owned or
                    // potentially owned transport. Never start a duplicate.
                    return;
                }
            }

            if (!shouldStart)
            {
                await StopKnownOrExternalAsync(channelId, cancellationToken);
                return;
            }

            try
            {
                bool started = await start(channelId, cancellationToken);
                lock (sync)
                {
                    ChannelState state = GetOrCreateState(channelId);
                    if (started)
                    {
                        state.Lifecycle = LifecycleState.Active;
                    }
                    else
                    {
                        RollBackIntent(state);
                        RemoveIfIdle(channelId, state);
                    }
                }
            }
            catch
            {
                lock (sync)
                {
                    ChannelState state = GetOrCreateState(channelId);
                    RollBackIntent(state);
                    RemoveIfIdle(channelId, state);
                }
                throw;
            }

        }
        finally
        {
            gate.Release();
        }
    }

    private async Task DisposeCoreAsync()
    {
        lock (sync)
            disposed = true;
        await ReleaseAllAsync(CancellationToken.None);
    }

    private async ValueTask StopKnownOrExternalAsync(
        ChannelId channelId,
        CancellationToken cancellationToken)
    {
        lock (sync)
        {
            ChannelState state = GetOrCreateState(channelId);
            if (state.Lifecycle == LifecycleState.Starting)
                throw new InvalidOperationException("Cannot stop PTT while its start transition still owns the gate.");
            state.Lifecycle = LifecycleState.StopPending;
        }

        try
        {
            await stop(channelId, cancellationToken);
            lock (sync)
            {
                if (!channels.TryGetValue(channelId, out ChannelState? state))
                    return;
                state.Lifecycle = LifecycleState.Idle;
                RemoveIfIdle(channelId, state);
            }
        }
        catch
        {
            lock (sync)
                GetOrCreateState(channelId).Lifecycle = LifecycleState.FailedStop;
            throw;
        }
    }

    private ChannelState GetOrCreateState(ChannelId channelId)
    {
        if (channels.TryGetValue(channelId, out ChannelState? state))
            return state;
        state = new ChannelState();
        channels.Add(channelId, state);
        return state;
    }

    private static void RollBackIntent(ChannelState state)
    {
        state.Held = false;
        state.Latched = false;
        state.Lifecycle = LifecycleState.Idle;
    }

    private void RemoveIfIdle(ChannelId channelId, ChannelState state)
    {
        if (!state.Requested && state.Lifecycle == LifecycleState.Idle)
            channels.Remove(channelId);
    }
}
