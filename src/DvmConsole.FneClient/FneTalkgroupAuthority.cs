namespace DvmConsole.FneClient;

public enum FneTalkgroupAvailability
{
    Pending,
    Available,
    Unavailable
}

public sealed record FneTalkgroupRule(
    uint DestinationId,
    byte Slot,
    bool IsActive,
    bool AffiliationRequired,
    bool NonPreferred);

public sealed class FneTalkgroupAuthority
{
    private readonly HashSet<uint> activeDestinationIds;
    private readonly HashSet<(uint DestinationId, byte Slot)> activeDmrDestinations;

    private FneTalkgroupAuthority(bool isAuthoritative, IEnumerable<FneTalkgroupRule> rules)
    {
        FneTalkgroupRule[] snapshot = rules.ToArray();
        IsAuthoritative = isAuthoritative;
        Rules = Array.AsReadOnly(snapshot);
        activeDestinationIds = snapshot
            .Where(rule => rule.IsActive)
            .Select(rule => rule.DestinationId)
            .ToHashSet();
        activeDmrDestinations = snapshot
            .Where(rule => rule.IsActive && rule.Slot is 1 or 2)
            .Select(rule => (rule.DestinationId, rule.Slot))
            .ToHashSet();
    }

    public static FneTalkgroupAuthority Pending { get; } = new(false, []);

    public bool IsAuthoritative { get; }
    public IReadOnlyList<FneTalkgroupRule> Rules { get; }

    public FneTalkgroupAvailability GetAvailability(
        FneTrafficProtocol protocol,
        uint destinationId,
        byte runtimeSlot)
    {
        if (!IsAuthoritative)
            return FneTalkgroupAvailability.Pending;

        bool available = protocol switch
        {
            FneTrafficProtocol.Dmr => activeDmrDestinations.Contains(
                (destinationId, checked((byte)(runtimeSlot + 1)))),
            FneTrafficProtocol.P25 or
            FneTrafficProtocol.Nxdn or
            FneTrafficProtocol.Analog => activeDestinationIds.Contains(destinationId),
            _ => throw new ArgumentOutOfRangeException(nameof(protocol))
        };
        return available
            ? FneTalkgroupAvailability.Available
            : FneTalkgroupAvailability.Unavailable;
    }

    public static FneTalkgroupAuthority FromRules(IEnumerable<FneTalkgroupRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return new FneTalkgroupAuthority(true, rules);
    }
}
