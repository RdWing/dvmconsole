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
    private readonly HashSet<string> activeStreams = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> recentlyEndedStreams = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> activeTargetUseCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> recentlyEndedSources = new(StringComparer.OrdinalIgnoreCase);

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

        string streamKey = BuildStreamKey(member, streamId);
        activeStreams.Add(streamKey);
        recentlyEndedStreams.Remove(streamKey);
        recentlyEndedSources.Remove(BuildSourceKey(member, outboundSourceId));
        activeTargetUseCounts[member.Key] = activeTargetUseCounts.GetValueOrDefault(member.Key) + 1;
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
        string streamKey = BuildStreamKey(member, streamId);
        activeStreams.Remove(streamKey);
        recentlyEndedStreams[streamKey] = suppressUntil;
        recentlyEndedSources[BuildSourceKey(member, outboundSourceId)] = suppressUntil;

        int remainingUseCount = activeTargetUseCounts.GetValueOrDefault(member.Key) - releasedUseCount;
        if (remainingUseCount > 0)
        {
            activeTargetUseCounts[member.Key] = remainingUseCount;
            return;
        }

        activeTargetUseCounts.Remove(member.Key);
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
        string streamKey = BuildStreamKey(member, streamId);
        return activeStreams.Contains(streamKey) ||
               recentlyEndedStreams.ContainsKey(streamKey) ||
               activeTargetUseCounts.ContainsKey(member.Key) ||
               recentlyEndedSources.ContainsKey(BuildSourceKey(member, sourceId));
    }

    public bool IsTargetActive(PatchMemberAddress member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return activeTargetUseCounts.ContainsKey(member.Key);
    }

    public void AllowReconfiguredSource(string memberKey)
    {
        if (string.IsNullOrWhiteSpace(memberKey))
            return;

        string prefix = $"{memberKey}|";
        foreach (string sourceKey in recentlyEndedSources.Keys
            .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToArray())
        {
            recentlyEndedSources.Remove(sourceKey);
        }
    }

    private void CleanupExpiredEntries()
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        RemoveExpired(recentlyEndedStreams, now);
        RemoveExpired(recentlyEndedSources, now);
    }

    private static void RemoveExpired(
        Dictionary<string, DateTimeOffset> entries,
        DateTimeOffset now)
    {
        foreach (string key in entries
            .Where(entry => entry.Value <= now)
            .Select(entry => entry.Key)
            .ToArray())
        {
            entries.Remove(key);
        }
    }

    private static string BuildStreamKey(PatchMemberAddress member, uint streamId)
        => $"{member.Key}|{streamId}";

    private static string BuildSourceKey(PatchMemberAddress member, uint sourceId)
        => $"{member.Key}|{sourceId}";
}
