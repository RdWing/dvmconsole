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
        ReadOnlySpan<byte> payload,
        ushort packetSequence,
        uint streamId);
}
