// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Runtime;

namespace DvmConsole.Application;

public sealed record TransmitChannelDescriptor(
    ChannelId Id,
    ChannelRuntimeDefinition Definition,
    bool ReceiveActive,
    bool TransmitEncrypted,
    bool CanTransmitByConfiguration,
    string ConfigurationUnavailableReason,
    string AuthorityUnavailableReason,
    bool AllowsTransmitDuringReceive = false)
{
    public string Name => Definition.Name;
}

public interface IRadioTrafficEndpoint
{
    string Name { get; }
    IReadOnlyCollection<TransmitChannelDescriptor> ChannelDescriptors { get; }
    IReadOnlyCollection<ChannelId> ChannelIds { get; }
    bool IsConnected { get; }
    uint? SourceId { get; }
    TargetAuthorityState GetTargetAuthority(
        RadioMediaProtocol protocol,
        uint destinationId,
        byte runtimeSlot);
    uint CreateStreamId();
    void SendTraffic(
        RadioMediaProtocol protocol,
        ReadOnlyMemory<byte> payload,
        ushort packetSequence,
        uint streamId);
}
