// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Diagnostics;
using DvmConsole.FneClient;

namespace DvmConsole.FneIntegration;

/// <summary>
/// FNE-specific capabilities layered over the portable radio-session
/// contract. Application coordinators consume <see cref="IRadioSession"/>;
/// legacy FNE presentation glue consumes only this adapter interface.
/// </summary>
public interface IFneRadioSession : IRadioSession, IRadioSessionAbort, IRadioDiagnosticSettings
{
    new bool IsConnected { get; }
    FneConnectionStatus Status { get; }

    event EventHandler<FneConnectionStatus>? StatusChanged;
    event EventHandler<FneLogEntry>? LogReceived;
    event EventHandler<FneKeyResponse>? KeyResponseReceived;

    Task StopAsync(CancellationToken cancellationToken = default);
    FneTalkgroupAvailability GetTalkgroupAvailability(
        FneTrafficProtocol protocol,
        uint destinationId,
        byte runtimeSlot);
    void SendTraffic(
        FneTrafficProtocol protocol,
        ReadOnlyMemory<byte> payload,
        ushort packetSequence,
        uint streamId);
    void RequestP25Key(byte algorithmId, ushort keyId);
    void SendP25SubscriberCommand(P25SubscriberCommand command, uint destinationId);
}

public interface IFneRadioSessionFactory
{
    RadioSystemDescriptor Descriptor { get; }
    IFneRadioSession Create();
}
