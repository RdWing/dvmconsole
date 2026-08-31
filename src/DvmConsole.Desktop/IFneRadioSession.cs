using DvmConsole.Application;
using DvmConsole.Core.Diagnostics;
using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

/// <summary>
/// Desktop-only FNE capabilities layered over the portable radio-session
/// contract. Application coordinators consume <see cref="IRadioSession"/>;
/// legacy FNE presentation glue consumes only this adapter interface.
/// </summary>
internal interface IFneRadioSession : IRadioSession
{
    new bool IsConnected { get; }
    FneConnectionStatus Status { get; }

    event EventHandler<FneConnectionStatus>? StatusChanged;
    event EventHandler<FneLogEntry>? LogReceived;
    event EventHandler<FneTrafficFrame>? FneTrafficReceived;
    event EventHandler<FneKeyResponse>? KeyResponseReceived;
    event EventHandler<FneTalkgroupAuthority>? FneTalkgroupAuthorityChanged;

    Task StopAsync(CancellationToken cancellationToken = default);
    void SetVerboseLogging(bool enabled);
    FneTalkgroupAvailability GetTalkgroupAvailability(
        FneTrafficProtocol protocol,
        uint destinationId,
        byte runtimeSlot);
    void SendTraffic(
        FneTrafficProtocol protocol,
        ReadOnlySpan<byte> payload,
        ushort packetSequence,
        uint streamId);
    void RequestP25Key(byte algorithmId, ushort keyId);
    void SendP25SubscriberCommand(P25SubscriberCommand command, uint destinationId);
}
