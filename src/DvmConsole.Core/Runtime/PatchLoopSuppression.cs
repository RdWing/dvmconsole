// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Core.Runtime;

// Owns loop-prevention state independently from patch membership and call
// routing. FNEs can echo a console transmission with a rewritten stream ID, so
// suppression covers exact outbound streams while a target is active. After
// teardown, rewritten echoes are identified by target and outbound source ID
// so a different subscriber can immediately begin the reverse patch leg.
internal sealed class PatchLoopSuppression
{
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan teardownWindow;
    private readonly HashSet<StreamKey> activeStreams = [];
    private readonly Dictionary<StreamKey, DateTimeOffset> recentlyEndedStreams = [];
    private readonly Dictionary<PatchMemberIdentity, int> activeTargetUseCounts = [];
    private readonly Dictionary<SourceKey, DateTimeOffset> recentlyEndedSources = [];
    private DateTimeOffset nextExpiry = DateTimeOffset.MaxValue;

    public PatchLoopSuppression(TimeProvider timeProvider, TimeSpan teardownWindow)
    {
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        if (teardownWindow < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(teardownWindow));
        this.teardownWindow = teardownWindow;
    }

    public void ActivateTarget(
        PatchMemberAddress member,
        uint streamId,
        uint outboundSourceId)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (streamId == 0)
            throw new ArgumentOutOfRangeException(nameof(streamId));

        var streamKey = new StreamKey(member.Identity, streamId);
        activeStreams.Add(streamKey);
        recentlyEndedStreams.Remove(streamKey);
        recentlyEndedSources.Remove(new SourceKey(member.Identity, outboundSourceId));
        activeTargetUseCounts[member.Identity] = activeTargetUseCounts.GetValueOrDefault(member.Identity) + 1;
    }

    public void ReleaseTarget(
        PatchMemberAddress member,
        uint streamId,
        uint outboundSourceId,
        int releasedUseCount = 1)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (streamId == 0)
            throw new ArgumentOutOfRangeException(nameof(streamId));
        if (releasedUseCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(releasedUseCount));

        DateTimeOffset suppressUntil = timeProvider.GetUtcNow() + teardownWindow;
        var streamKey = new StreamKey(member.Identity, streamId);
        activeStreams.Remove(streamKey);
        recentlyEndedStreams[streamKey] = suppressUntil;
        recentlyEndedSources[new SourceKey(member.Identity, outboundSourceId)] = suppressUntil;
        if (suppressUntil < nextExpiry)
            nextExpiry = suppressUntil;

        int remainingUseCount = activeTargetUseCounts.GetValueOrDefault(member.Identity) - releasedUseCount;
        if (remainingUseCount > 0)
        {
            activeTargetUseCounts[member.Identity] = remainingUseCount;
            return;
        }

        activeTargetUseCounts.Remove(member.Identity);
    }

    public bool ShouldSuppressInbound(
        PatchMemberAddress member,
        uint streamId,
        uint sourceId)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (streamId == 0)
            return false;

        CleanupExpiredEntries();
        var streamKey = new StreamKey(member.Identity, streamId);
        return activeStreams.Contains(streamKey) ||
               recentlyEndedStreams.ContainsKey(streamKey) ||
               activeTargetUseCounts.ContainsKey(member.Identity) ||
               recentlyEndedSources.ContainsKey(new SourceKey(member.Identity, sourceId));
    }

    public bool IsTargetActive(PatchMemberAddress member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return activeTargetUseCounts.ContainsKey(member.Identity);
    }

    public void AllowReconfiguredSource(PatchMemberIdentity member)
    {
        List<SourceKey>? removals = null;
        foreach (SourceKey sourceKey in recentlyEndedSources.Keys)
        {
            if (sourceKey.Member != member)
                continue;
            (removals ??= []).Add(sourceKey);
        }
        if (removals is null)
            return;
        foreach (SourceKey sourceKey in removals)
            recentlyEndedSources.Remove(sourceKey);
    }

    private void CleanupExpiredEntries()
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (now < nextExpiry)
            return;
        RemoveExpired(recentlyEndedStreams, now);
        RemoveExpired(recentlyEndedSources, now);
        nextExpiry = FindNextExpiry();
    }

    private static void RemoveExpired<TKey>(
        Dictionary<TKey, DateTimeOffset> entries,
        DateTimeOffset now)
        where TKey : notnull
    {
        List<TKey>? removals = null;
        foreach ((TKey key, DateTimeOffset expiry) in entries)
        {
            if (expiry <= now)
                (removals ??= []).Add(key);
        }
        if (removals is null)
            return;
        foreach (TKey key in removals)
            entries.Remove(key);
    }

    private DateTimeOffset FindNextExpiry()
    {
        DateTimeOffset next = DateTimeOffset.MaxValue;
        foreach (DateTimeOffset expiry in recentlyEndedStreams.Values)
            if (expiry < next)
                next = expiry;
        foreach (DateTimeOffset expiry in recentlyEndedSources.Values)
            if (expiry < next)
                next = expiry;
        return next;
    }

    private readonly record struct StreamKey(PatchMemberIdentity Member, uint StreamId);
    private readonly record struct SourceKey(PatchMemberIdentity Member, uint SourceId);
}
