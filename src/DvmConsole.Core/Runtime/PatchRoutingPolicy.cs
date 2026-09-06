// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Core.Runtime;

public readonly record struct PatchCallStartResult(uint StreamId, bool SkipSourceCall = false)
{
    public static PatchCallStartResult Skipped => new(0, true);
}

public interface IPatchForwardingSink
{
    uint BeginCall(PatchMemberAddress member, uint sourceId);
    PatchCallStartResult TryBeginCall(PatchMemberAddress member, uint sourceId)
        => new(BeginCall(member, sourceId));
    void EndCall(PatchMemberAddress member, uint streamId, uint sourceId);
    void SendAudio(PatchMemberAddress member, uint streamId, ReadOnlyMemory<short> samples, uint sourceId);
    uint GetFallbackSourceId(PatchMemberAddress member);
}

internal sealed record PatchGroupMembership(
    string GroupName,
    IReadOnlyList<PatchMemberAddress> Members,
    bool OneWay);

internal static class PatchMembershipPolicy
{
    public static Dictionary<string, PatchGroupMembership> Normalize(
        IReadOnlyDictionary<string, IReadOnlyList<PatchMemberAddress>> memberships,
        IReadOnlyDictionary<string, bool>? oneWayModes)
    {
        var normalized = new Dictionary<string, PatchGroupMembership>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, IReadOnlyList<PatchMemberAddress> configuredMembers) in memberships ??
                 new Dictionary<string, IReadOnlyList<PatchMemberAddress>>())
        {
            string groupName = name?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(groupName))
                continue;

            List<PatchMemberAddress> members = (configuredMembers ?? [])
                .Where(member => member is not null)
                .GroupBy(member => member.Identity)
                .Select(group => group.First())
                .ToList();
            if (members.Count == 0)
                continue;

            bool oneWay = oneWayModes is not null &&
                oneWayModes.TryGetValue(groupName, out bool configuredOneWay) &&
                configuredOneWay;
            normalized[groupName] = new PatchGroupMembership(groupName, members, oneWay);
        }

        return normalized;
    }

    public static bool MembersEqual(
        IReadOnlyList<PatchMemberAddress> left,
        IReadOnlyList<PatchMemberAddress> right)
        => new HashSet<PatchMemberIdentity>(left.Select(member => member.Identity))
            .SetEquals(right.Select(member => member.Identity));

    public static bool RoutingEqual(
        PatchGroupMembership left,
        PatchGroupMembership right)
        => left.OneWay == right.OneWay &&
           MembersEqual(left.Members, right.Members) &&
           (!left.OneWay || left.Members[0].Identity == right.Members[0].Identity);

    public static bool IsEligibleSource(
        IReadOnlyList<PatchMemberAddress> members,
        bool oneWay,
        PatchMemberAddress source)
    {
        if (oneWay)
            return members.Count > 0 && members[0].Identity == source.Identity;

        for (int index = 0; index < members.Count; index++)
        {
            if (members[index].Identity == source.Identity)
                return true;
        }
        return false;
    }
}

internal sealed class DelegatePatchForwardingSink : IPatchForwardingSink
{
    private readonly Func<PatchMemberAddress, uint, PatchCallStartResult> beginCall;
    private readonly Action<PatchMemberAddress, uint, uint> endCall;
    private readonly Action<PatchMemberAddress, uint, ReadOnlyMemory<short>, uint> sendAudio;
    private readonly Func<PatchMemberAddress, uint> fallbackSourceId;

    public DelegatePatchForwardingSink(
        Func<PatchMemberAddress, uint, uint> beginCall,
        Action<PatchMemberAddress, uint, uint> endCall,
        Action<PatchMemberAddress, uint, ReadOnlyMemory<short>, uint> sendAudio,
        Func<PatchMemberAddress, uint> fallbackSourceId)
        : this((member, sourceId) => new PatchCallStartResult(beginCall(member, sourceId)),
            endCall, sendAudio, fallbackSourceId)
    {
        ArgumentNullException.ThrowIfNull(beginCall);
    }

    public DelegatePatchForwardingSink(
        Func<PatchMemberAddress, uint, PatchCallStartResult> beginCall,
        Action<PatchMemberAddress, uint, uint> endCall,
        Action<PatchMemberAddress, uint, ReadOnlyMemory<short>, uint> sendAudio,
        Func<PatchMemberAddress, uint> fallbackSourceId)
    {
        this.beginCall = beginCall ?? throw new ArgumentNullException(nameof(beginCall));
        this.endCall = endCall ?? throw new ArgumentNullException(nameof(endCall));
        this.sendAudio = sendAudio ?? throw new ArgumentNullException(nameof(sendAudio));
        this.fallbackSourceId = fallbackSourceId ?? throw new ArgumentNullException(nameof(fallbackSourceId));
    }

    public uint BeginCall(PatchMemberAddress member, uint sourceId)
        => beginCall(member, sourceId).StreamId;

    public PatchCallStartResult TryBeginCall(PatchMemberAddress member, uint sourceId)
        => beginCall(member, sourceId);

    public void EndCall(PatchMemberAddress member, uint streamId, uint sourceId)
        => endCall(member, streamId, sourceId);

    public void SendAudio(PatchMemberAddress member, uint streamId, ReadOnlyMemory<short> samples, uint sourceId)
        => sendAudio(member, streamId, samples, sourceId);

    public uint GetFallbackSourceId(PatchMemberAddress member)
        => fallbackSourceId(member);
}
