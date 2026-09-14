// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>Per-connection request deduplication and response tracking; stores no key material.</summary>
public sealed class P25KeyRequestState
{
    private readonly object sync = new();
    private readonly HashSet<(byte AlgorithmId, ushort KeyId)> requested = [];
    private readonly HashSet<(byte AlgorithmId, ushort KeyId)> received = [];

    public void Request(byte algorithmId, ushort keyId, Action send)
    {
        ArgumentNullException.ThrowIfNull(send);
        lock (sync)
        {
            if (!requested.Add((algorithmId, keyId))) return;
        }
        try { send(); }
        catch
        {
            lock (sync) requested.Remove((algorithmId, keyId));
            throw;
        }
    }

    public void Retry(byte algorithmId, ushort keyId, Action send)
    {
        ArgumentNullException.ThrowIfNull(send);
        lock (sync)
        {
            if (received.Contains((algorithmId, keyId))) return;
        }
        send();
    }

    public bool HasResponse(byte algorithmId, ushort keyId)
    {
        lock (sync) return received.Contains((algorithmId, keyId));
    }

    public void ObserveResponse(byte algorithmId, ushort keyId)
    {
        lock (sync) received.Add((algorithmId, keyId));
    }

    public void Clear()
    {
        lock (sync)
        {
            requested.Clear();
            received.Clear();
        }
    }
}
