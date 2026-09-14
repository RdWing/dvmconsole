// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Media;

namespace DvmConsole.Application;

/// <summary>Owns configured key requests and the connection-scoped FNE key layer.</summary>
internal sealed class P25KeyRetrievalCoordinator : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly P25KeyRing keys;
    private readonly P25KeyRequestCoordinator requests;
    private readonly Dictionary<string, Connection> connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly AsyncDisposal disposal = new();
    private bool disposed;

    public event EventHandler? KeysChanged;

    public P25KeyRetrievalCoordinator(P25KeyRing keys,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.keys = keys ?? throw new ArgumentNullException(nameof(keys));
        requests = delay is null ? new() : new(delay);
    }

    public Task Schedule(string systemName, IReadOnlyList<(byte AlgorithmId, ushort KeyId)> configured,
        Func<bool> isConnected, P25KeyRequestPort endpoint, Action<Exception> reportFailure)
        => Schedule(systemName, configured, isConnected, endpoint.Request, endpoint.HasResponse,
            endpoint.Retry, reportFailure);

    public Task Schedule(string systemName, IReadOnlyList<(byte AlgorithmId, ushort KeyId)> configured,
        Func<bool> isConnected, Action<byte, ushort> requestKey,
        Func<byte, ushort, bool> hasResponse, Action<byte, ushort> retryKey,
        Action<Exception> reportFailure)
    {
        var connection = new Connection(isConnected);
        bool IsCurrent() => !disposed && connections.TryGetValue(systemName, out var current) &&
            ReferenceEquals(connection, current) && isConnected();
        bool CanRequest() { lock (sync) return IsCurrent(); }
        void Send(Action<byte, ushort> send, byte algorithm, ushort key)
        {
            lock (sync)
            {
                if (IsCurrent()) send(algorithm, key);
            }
        }
        lock (sync)
        {
            if (disposed) return Task.CompletedTask;
            connections[systemName] = connection;
            return requests.Schedule(systemName, configured, CanRequest,
                (algorithm, key) => Send(requestKey, algorithm, key), hasResponse,
                (algorithm, key) => Send(retryKey, algorithm, key), reportFailure);
        }
    }

    public bool TryApply(string systemName, byte algorithmId, ushort keyId, ReadOnlySpan<byte> material, Action? onAccepted = null)
    {
        lock (sync)
        {
            if (disposed || !connections.TryGetValue(systemName, out var connection) || !connection.IsConnected())
                return false;
            keys.AddOrReplaceFromFne(systemName, algorithmId, keyId, material);
            onAccepted?.Invoke();
        }
        PublishKeysChanged();
        return true;
    }

    private void PublishKeysChanged()
    {
        foreach (EventHandler observer in KeysChanged?.GetInvocationList() ?? [])
        {
            try { observer(this, EventArgs.Empty); }
            catch { /* Key lifetime cannot depend on an attached presentation. */ }
        }
    }

    public void Cancel(string systemName)
    {
        bool changed;
        lock (sync)
        {
            changed = connections.Remove(systemName);
            if (!disposed) keys.ClearFneKeys(systemName);
            requests.Cancel(systemName);
        }
        if (changed) PublishKeysChanged();
    }

    public void Pause()
    {
        bool changed;
        lock (sync)
        {
            changed = connections.Count != 0;
            foreach (string name in connections.Keys)
            {
                if (!disposed) keys.ClearFneKeys(name);
                requests.Cancel(name);
            }
            connections.Clear();
        }
        if (changed) PublishKeysChanged();
    }

    public ValueTask DisposeAsync() => disposal.RunAsync(async () =>
    {
        lock (sync)
        {
            disposed = true;
            foreach (string name in connections.Keys) keys.ClearFneKeys(name);
            connections.Clear();
        }
        await requests.DisposeAsync().ConfigureAwait(false);
    });

    private sealed record Connection(Func<bool> IsConnected);
}
