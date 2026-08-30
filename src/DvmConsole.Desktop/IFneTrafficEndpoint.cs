using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

// Narrow coordinator boundary. Production uses SystemViewModel; tests can use
// a connected in-memory endpoint without opening a UDP FNE peer.
public interface IFneTrafficEndpoint
{
    string Name { get; }
    IReadOnlyList<ChannelViewModel> Channels { get; }
    bool IsConnected { get; }
    uint? SourceId { get; }
    FneTalkgroupAvailability GetTalkgroupAvailability(
        FneTrafficProtocol protocol,
        uint destinationId,
        byte runtimeSlot);
    uint CreateStreamId();
    void SendTraffic(FneTrafficProtocol protocol, ReadOnlySpan<byte> payload, ushort packetSequence, uint streamId);
}
