using DvmConsole.Core.Runtime;

namespace DvmConsole.Operations;

/// <summary>
/// Stable, presentation-independent identity for one configured channel instance.
/// Values are normalized so the identity can safely key long-lived runtime state.
/// </summary>
public readonly record struct ChannelSessionId
{
    public ChannelSessionId(
        string systemName,
        ChannelProtocol protocol,
        uint destinationId,
        byte slot,
        string instanceKey)
    {
        if (string.IsNullOrWhiteSpace(systemName))
            throw new ArgumentException("A system name is required.", nameof(systemName));
        if (destinationId == 0)
            throw new ArgumentOutOfRangeException(nameof(destinationId));
        if (protocol == ChannelProtocol.Dmr && slot > 1)
            throw new ArgumentOutOfRangeException(nameof(slot));
        if (string.IsNullOrWhiteSpace(instanceKey))
            throw new ArgumentException("An instance key is required.", nameof(instanceKey));

        SystemName = Normalize(systemName);
        Protocol = protocol;
        DestinationId = destinationId;
        Slot = protocol == ChannelProtocol.Dmr ? slot : (byte)0;
        InstanceKey = Normalize(instanceKey);
    }

    public string SystemName { get; }
    public ChannelProtocol Protocol { get; }
    public uint DestinationId { get; }
    public byte Slot { get; }
    public string InstanceKey { get; }

    public ChannelRouteKey RouteKey => new(SystemName, Protocol, DestinationId, Slot);

    public override string ToString()
        => $"{SystemName}/{Protocol.ToString().ToLowerInvariant()}/{DestinationId}/{Slot}/{InstanceKey}";

    private static string Normalize(string value)
        => value.Trim().ToLowerInvariant();
}

/// <summary>
/// Stable receive-routing identity shared by visual copies of the same resource.
/// </summary>
public readonly record struct ChannelRouteKey
{
    public ChannelRouteKey(
        string systemName,
        ChannelProtocol protocol,
        uint destinationId,
        byte slot)
    {
        if (string.IsNullOrWhiteSpace(systemName))
            throw new ArgumentException("A system name is required.", nameof(systemName));
        if (destinationId == 0)
            throw new ArgumentOutOfRangeException(nameof(destinationId));
        if (protocol == ChannelProtocol.Dmr && slot > 1)
            throw new ArgumentOutOfRangeException(nameof(slot));

        SystemName = systemName.Trim().ToLowerInvariant();
        Protocol = protocol;
        DestinationId = destinationId;
        Slot = protocol == ChannelProtocol.Dmr ? slot : (byte)0;
    }

    public string SystemName { get; }
    public ChannelProtocol Protocol { get; }
    public uint DestinationId { get; }
    public byte Slot { get; }
}

/// <summary>
/// Immutable operational description consumed by coordinators and presentation adapters.
/// </summary>
public sealed record ChannelDefinition(
    ChannelSessionId SessionId,
    string Name,
    bool RxOnly,
    bool IsEncrypted)
{
    public string SystemName => SessionId.SystemName;
    public ChannelProtocol Protocol => SessionId.Protocol;
    public uint DestinationId => SessionId.DestinationId;
    public byte Slot => SessionId.Slot;
    public ChannelRouteKey RouteKey => SessionId.RouteKey;

    public static ChannelDefinition FromRuntime(
        ChannelRuntimeDefinition definition,
        string instanceKey)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new ChannelDefinition(
            new ChannelSessionId(
                definition.SystemName,
                definition.Protocol,
                definition.DestinationId,
                definition.Slot,
                instanceKey),
            definition.Name,
            definition.RxOnly,
            definition.IsEncrypted);
    }
}
