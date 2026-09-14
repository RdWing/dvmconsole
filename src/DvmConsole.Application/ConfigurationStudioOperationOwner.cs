// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>Serializes draft checkpoints, reviewed saves and retirement without replaying stale edits.</summary>
public sealed class ConfigurationStudioOperationOwner
{
    private readonly object sync = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly AsyncDisposal disposal = new();
    private long generation;
    private bool closing;

    public async Task<bool> CheckpointAsync(Func<CancellationToken, Task> checkpoint, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        long requested;
        lock (sync)
        {
            if (closing) return false;
            requested = generation;
        }
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (sync) if (closing || requested != generation) return false;
            await checkpoint(token).ConfigureAwait(false);
            return true;
        }
        finally { gate.Release(); }
    }

    public async Task<T> SaveAsync<T>(Func<Task<T>> save)
    {
        ArgumentNullException.ThrowIfNull(save);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(closing, this);
            generation++;
        }
        await gate.WaitAsync();
        try
        {
            lock (sync) ObjectDisposedException.ThrowIf(closing, this);
            // Retain the caller's context inside the save callback: accepting a
            // committed revision may update bound editor state.
            return await save().ConfigureAwait(false);
        }
        finally
        {
            lock (sync) generation++;
            gate.Release();
        }
    }

    public ValueTask CloseAsync(Func<Task> retire)
    {
        ArgumentNullException.ThrowIfNull(retire);
        lock (sync) { closing = true; generation++; }
        return disposal.RunAsync(async () =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try { await retire().ConfigureAwait(false); }
            finally { gate.Release(); }
        });
    }
}
