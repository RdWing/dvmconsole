using DvmConsole.Application;
using DvmConsole.Core.Runtime;
using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

// ID-only radio boundary. No presentation object crosses into transmit,
// generated-audio, or patch routing services.
public interface IFneTrafficEndpoint : IRadioTrafficEndpoint
{
    new string Name { get; }
    new IReadOnlyCollection<TransmitChannelDescriptor> ChannelDescriptors { get; }
    new IReadOnlyCollection<ChannelId> ChannelIds { get; }
    new bool IsConnected { get; }
    new uint? SourceId { get; }
    FneTalkgroupAvailability GetTalkgroupAvailability(
        FneTrafficProtocol protocol,
        uint destinationId,
        byte runtimeSlot);
    new uint CreateStreamId();
    void SendTraffic(FneTrafficProtocol protocol, ReadOnlySpan<byte> payload, ushort packetSequence, uint streamId);

    string IRadioTrafficEndpoint.Name => Name;
    IReadOnlyCollection<TransmitChannelDescriptor> IRadioTrafficEndpoint.ChannelDescriptors
        => ChannelDescriptors;
    IReadOnlyCollection<ChannelId> IRadioTrafficEndpoint.ChannelIds => ChannelIds;
    bool IRadioTrafficEndpoint.IsConnected => IsConnected;
    uint? IRadioTrafficEndpoint.SourceId => SourceId;
    uint IRadioTrafficEndpoint.CreateStreamId() => CreateStreamId();

    TargetAuthorityState IRadioTrafficEndpoint.GetTargetAuthority(
        RadioMediaProtocol protocol,
        uint destinationId,
        byte runtimeSlot)
    {
        FneTalkgroupAvailability availability = GetTalkgroupAvailability(
            protocol switch
            {
                RadioMediaProtocol.Dmr => FneTrafficProtocol.Dmr,
                RadioMediaProtocol.P25 => FneTrafficProtocol.P25,
                RadioMediaProtocol.Nxdn => FneTrafficProtocol.Nxdn,
                RadioMediaProtocol.Analog => FneTrafficProtocol.Analog,
                _ => throw new ArgumentOutOfRangeException(nameof(protocol))
            },
            destinationId,
            runtimeSlot);
        return availability switch
        {
            FneTalkgroupAvailability.Available => TargetAuthorityState.Available,
            FneTalkgroupAvailability.Unavailable => TargetAuthorityState.Unavailable,
            _ => TargetAuthorityState.Pending
        };
    }

    void IRadioTrafficEndpoint.SendTraffic(
        RadioMediaProtocol protocol,
        ReadOnlySpan<byte> payload,
        ushort packetSequence,
        uint streamId)
        => SendTraffic(
            protocol switch
            {
                RadioMediaProtocol.Dmr => FneTrafficProtocol.Dmr,
                RadioMediaProtocol.P25 => FneTrafficProtocol.P25,
                RadioMediaProtocol.Nxdn => FneTrafficProtocol.Nxdn,
                RadioMediaProtocol.Analog => FneTrafficProtocol.Analog,
                _ => throw new ArgumentOutOfRangeException(nameof(protocol))
            },
            payload,
            packetSequence,
            streamId);
}
