// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>Connects shared request/response tracking to a radio's key request operation.</summary>
internal sealed class P25KeyRequestPort(P25KeyRequestState state, Action<byte, ushort> send,
    Action<byte, ushort, bool>? observe = null)
{
    public void Request(byte algorithm, ushort key)
        => state.Request(algorithm, key, () => Send(algorithm, key, retry: false));

    public void Retry(byte algorithm, ushort key)
        => state.Retry(algorithm, key, () => Send(algorithm, key, retry: true));

    public bool HasResponse(byte algorithm, ushort key) => state.HasResponse(algorithm, key);

    private void Send(byte algorithm, ushort key, bool retry)
    {
        send(algorithm, key);
        observe?.Invoke(algorithm, key, retry);
    }
}
