// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

/// <summary>Sanitized key material delivered by the transport's current request generation.</summary>
public sealed record RadioP25KeyResponse(SystemId SystemId, byte AlgorithmId, ushort KeyId,
    ReadOnlyMemory<byte> KeyMaterial);

public interface IRadioP25KeyEndpoint
{
    event EventHandler<RadioP25KeyResponse>? P25KeyReceived;
    void RequestP25Key(byte algorithmId, ushort keyId);
}
