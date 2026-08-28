using DvmConsole.Core.Settings;
using DvmConsole.FneClient;
using DvmConsole.Operations;

namespace DvmConsole.Desktop;

internal static class ReceiveCallEpisodePolicy
{
    private static readonly TimeSpan P25PacketDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan DmrPacketDuration = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan NxdnPacketDuration = TimeSpan.FromMilliseconds(80);

    // A replacement stream belongs to the same logical call only while it can
    // still plausibly be delayed media from the configured receive horizon.
    // One additional protocol packet admits a boundary packet without turning
    // ordinary later push-to-talk activity into the previous call.
    public static TimeSpan GetContinuationWindow(FneTrafficProtocol protocol)
        => protocol switch
        {
            FneTrafficProtocol.P25 => TimeSpan.FromMilliseconds(
                RxJitterBufferSetting.MaximumP25Milliseconds) + P25PacketDuration,
            FneTrafficProtocol.Dmr => TimeSpan.FromMilliseconds(
                RxJitterBufferSetting.MaximumDmrMilliseconds) + DmrPacketDuration,
            FneTrafficProtocol.Nxdn => TimeSpan.FromMilliseconds(
                RxJitterBufferSetting.MaximumNxdnMilliseconds) + NxdnPacketDuration,
            _ => TimeSpan.Zero
        };
}

internal readonly record struct ReceiveCallEpisodeObservation(
    long EpisodeId,
    uint PrimaryStreamId,
    bool EpisodeStarted,
    bool StreamAdded,
    int StreamCount);

internal enum ReceivePhysicalEndReason
{
    Replaced,
    InactivityTimeout,
    ConfirmedTerminator
}

internal sealed record ReceiveCallEpisodeSnapshot(
    long EpisodeId,
    string SystemName,
    FneTrafficProtocol Protocol,
    uint SourceId,
    uint DestinationId,
    byte? Slot,
    string CallType,
    uint PrimaryStreamId,
    IReadOnlyList<uint> StreamIds,
    DateTimeOffset StartedAt,
    DateTimeOffset LastActivityAt,
    DateTimeOffset PresentationEndAt,
    EncryptionSnapshot Encryption);

// Correlates unstable physical FNE stream IDs without changing decoder,
// lifecycle, or wire ownership. Physical stream mappings remain available for
// a short tombstone period so queued audio and late terminators resolve to the
// same episode after its presentation row has completed.
internal sealed class ReceiveCallEpisodeTracker
{
    private static readonly TimeSpan MappingRetention = TimeSpan.FromSeconds(5);
    internal const int MaximumTrackedEpisodes = 256;
    internal const int MaximumStreamsPerEpisode =
        ReceiveStreamPolicy.DefaultMaximumTrackedStreams;
    private readonly object sync = new();
    private readonly Dictionary<ReceiveCallIdentity, List<EpisodeState>> currentByIdentity = [];
    private readonly Dictionary<PhysicalStreamKey, EpisodeState> byPhysicalStream = [];
    private readonly Dictionary<long, EpisodeState> episodes = [];
    private long nextEpisodeId;

    public ReceiveCallEpisodeObservation? Observe(
        string systemName,
        FneTrafficFrame traffic,
        DateTimeOffset observedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemName);
        ArgumentNullException.ThrowIfNull(traffic);
        if (traffic.StreamId == 0)
            return null;

        lock (sync)
        {
            PurgeExpiredMappings(observedAt);
            var physicalKey = new PhysicalStreamKey(systemName, traffic.Protocol, traffic.StreamId);
            bool definitiveRestart = ReceiveTrafficClassifier.IsDefinitiveStart(traffic);
            if (byPhysicalStream.TryGetValue(physicalKey, out EpisodeState? mapped))
            {
                bool hasCompleteIdentity = traffic.SourceId != 0 && traffic.DestinationId != 0;
                if (hasCompleteIdentity &&
                    mapped.Identity != ReceiveCallIdentity.Create(systemName, traffic))
                {
                    // A reused physical ID cannot override the stronger call
                    // identity. In particular, a different source remains a
                    // different episode even when the FNE repeats a stream ID.
                    byPhysicalStream.Remove(physicalKey);
                    mapped = null;
                }
            }

            if (mapped is not null)
            {
                TimeSpan continuationWindow = ReceiveCallEpisodePolicy.GetContinuationWindow(
                    traffic.Protocol);
                EncryptionSnapshot restartEncryption =
                    EncryptionSnapshotResolver.TryResolve(traffic) ?? EncryptionSnapshot.Unknown;
                bool restartAfterConfirmedEnd =
                    definitiveRestart &&
                    mapped.LastPhysicalEndReason == ReceivePhysicalEndReason.ConfirmedTerminator;
                bool restartMustEstablishSecurity =
                    definitiveRestart &&
                    mapped.LastPhysicalEndAt is not null &&
                    mapped.Encryption.IsKnown &&
                    !restartEncryption.IsKnown;
                if (!restartAfterConfirmedEnd &&
                    !restartMustEstablishSecurity &&
                    (!definitiveRestart ||
                    continuationWindow <= TimeSpan.Zero ||
                    observedAt - mapped.LastActivityAt <= continuationWindow))
                {
                    ObserveMappedTraffic(mapped, traffic, observedAt);
                    return CreateObservation(mapped, episodeStarted: false, streamAdded: false);
                }
                byPhysicalStream.Remove(physicalKey);
            }

            if (!CanStartOrContinueEpisode(traffic) ||
                traffic.SourceId == 0 ||
                traffic.DestinationId == 0)
            {
                return null;
            }

            TimeSpan window = ReceiveCallEpisodePolicy.GetContinuationWindow(traffic.Protocol);
            var identity = ReceiveCallIdentity.Create(systemName, traffic);
            EncryptionSnapshot encryption =
                EncryptionSnapshotResolver.TryResolve(traffic) ?? EncryptionSnapshot.Unknown;
            EpisodeState? episode = window <= TimeSpan.Zero
                ? null
                : FindContinuation(identity, encryption, observedAt, window, definitiveRestart);
            bool episodeStarted = episode is null;
            if (episode is null)
            {
                MakeRoomForEpisode();
                episode = new EpisodeState(
                    checked(++nextEpisodeId),
                    identity,
                    traffic.StreamId,
                    observedAt,
                    encryption);
                episodes.Add(episode.EpisodeId, episode);
                if (!currentByIdentity.TryGetValue(identity, out List<EpisodeState>? identityEpisodes))
                {
                    identityEpisodes = [];
                    currentByIdentity.Add(identity, identityEpisodes);
                }
                identityEpisodes.Add(episode);
            }

            EpisodeStreamUpdate streamUpdate = episode.TrackStream(
                traffic.StreamId,
                MaximumStreamsPerEpisode);
            if (streamUpdate.RemovedStreamId is uint removedStreamId)
            {
                byPhysicalStream.Remove(new PhysicalStreamKey(
                    systemName,
                    traffic.Protocol,
                    removedStreamId));
            }
            if (streamUpdate.Added)
            {
                episode.LastPhysicalEndAt = null;
                episode.LastPhysicalEndReason = null;
            }
            episode.LastActivityAt = observedAt;
            episode.ObserveEncryption(traffic);
            byPhysicalStream[physicalKey] = episode;
            return CreateObservation(episode, episodeStarted, streamUpdate.Added);
        }
    }

    public bool TryGet(
        string systemName,
        FneTrafficProtocol protocol,
        uint physicalStreamId,
        out ReceiveCallEpisodeSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemName);
        lock (sync)
        {
            var key = new PhysicalStreamKey(systemName, protocol, physicalStreamId);
            if (byPhysicalStream.TryGetValue(key, out EpisodeState? episode))
            {
                snapshot = episode.Snapshot();
                return true;
            }
        }

        snapshot = null!;
        return false;
    }

    public void ObservePhysicalEnd(
        string systemName,
        FneTrafficProtocol protocol,
        uint physicalStreamId,
        DateTimeOffset endedAt,
        ReceivePhysicalEndReason reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemName);
        lock (sync)
        {
            var key = new PhysicalStreamKey(systemName, protocol, physicalStreamId);
            if (byPhysicalStream.TryGetValue(key, out EpisodeState? episode))
                episode.ObservePhysicalEnd(endedAt, reason);
        }
    }

    public IReadOnlyList<ReceiveCallEpisodeSnapshot> Advance(
        DateTimeOffset now,
        Func<ReceiveCallEpisodeSnapshot, bool>? canComplete = null)
    {
        lock (sync)
        {
            var completed = new List<ReceiveCallEpisodeSnapshot>();
            foreach (EpisodeState episode in episodes.Values
                         .Where(candidate => candidate.CompletedAt is null)
                         .OrderBy(candidate => candidate.LastActivityAt)
                         .ToArray())
            {
                TimeSpan window = ReceiveCallEpisodePolicy.GetContinuationWindow(
                    episode.Identity.Protocol);
                if (window > TimeSpan.Zero && now - episode.LastActivityAt <= window)
                    continue;
                if (canComplete is not null && !canComplete(episode.Snapshot()))
                    continue;

                episode.CompletedAt = now;
                episode.MappingExpiresAt = now + MappingRetention;
                RemoveCurrentEpisode(episode);
                completed.Add(episode.Snapshot());
            }

            PurgeExpiredMappings(now);
            return completed;
        }
    }

    public void Reset(string systemName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemName);
        lock (sync)
        {
            foreach (EpisodeState episode in episodes.Values
                         .Where(candidate => candidate.Identity.SystemName.Equals(
                             systemName,
                             StringComparison.OrdinalIgnoreCase))
                         .ToArray())
            {
                RemoveEpisode(episode);
            }
        }
    }

    private static bool CanStartOrContinueEpisode(FneTrafficFrame traffic)
        => ReceiveTrafficClassifier.CarriesVoicePayload(traffic) ||
           ReceiveTrafficClassifier.IsDefinitiveStart(traffic) ||
           ReceiveTrafficClassifier.IsDmrPrivacyHeader(traffic);

    private EpisodeState? FindContinuation(
        ReceiveCallIdentity identity,
        EncryptionSnapshot encryption,
        DateTimeOffset observedAt,
        TimeSpan window,
        bool definitiveRestart)
    {
        if (!currentByIdentity.TryGetValue(identity, out List<EpisodeState>? candidates))
            return null;

        return candidates
            .Where(candidate =>
                candidate.CompletedAt is null &&
                (!definitiveRestart ||
                 candidate.LastPhysicalEndReason != ReceivePhysicalEndReason.ConfirmedTerminator) &&
                observedAt >= candidate.LastActivityAt &&
                observedAt - candidate.LastActivityAt <= window &&
                candidate.IsEncryptionCompatible(encryption))
            .OrderByDescending(candidate => candidate.LastActivityAt)
            .FirstOrDefault();
    }

    private static void ObserveMappedTraffic(
        EpisodeState episode,
        FneTrafficFrame traffic,
        DateTimeOffset observedAt)
    {
        if (CanStartOrContinueEpisode(traffic) && observedAt > episode.LastActivityAt)
            episode.LastActivityAt = observedAt;
        if (ReceiveTrafficClassifier.IsTerminator(traffic))
        {
            episode.ObservePhysicalEnd(
                observedAt,
                ReceivePhysicalEndReason.ConfirmedTerminator);
        }
        episode.ObserveEncryption(traffic);
    }

    private static ReceiveCallEpisodeObservation CreateObservation(
        EpisodeState episode,
        bool episodeStarted,
        bool streamAdded)
        => new(
            episode.EpisodeId,
            episode.PrimaryStreamId,
            episodeStarted,
            streamAdded,
            episode.StreamIds.Count);

    private void RemoveCurrentEpisode(EpisodeState episode)
    {
        if (!currentByIdentity.TryGetValue(
                episode.Identity,
                out List<EpisodeState>? identityEpisodes))
        {
            return;
        }

        identityEpisodes.Remove(episode);
        if (identityEpisodes.Count == 0)
            currentByIdentity.Remove(episode.Identity);
    }

    private void PurgeExpiredMappings(DateTimeOffset now)
    {
        foreach (EpisodeState episode in episodes.Values
                     .Where(candidate =>
                         candidate.MappingExpiresAt is DateTimeOffset expiresAt &&
                         expiresAt <= now)
                     .ToArray())
        {
            RemoveEpisode(episode);
        }
    }

    private void MakeRoomForEpisode()
    {
        if (episodes.Count < MaximumTrackedEpisodes)
            return;

        EpisodeState oldest = episodes.Values
            .OrderBy(candidate => candidate.CompletedAt is null ? 1 : 0)
            .ThenBy(candidate => candidate.LastActivityAt)
            .ThenBy(candidate => candidate.EpisodeId)
            .First();
        RemoveEpisode(oldest);
    }

    private void RemoveEpisode(EpisodeState episode)
    {
        RemoveCurrentEpisode(episode);
        episodes.Remove(episode.EpisodeId);
        foreach (PhysicalStreamKey key in byPhysicalStream
                     .Where(pair => ReferenceEquals(pair.Value, episode))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            byPhysicalStream.Remove(key);
        }
    }

    private readonly record struct PhysicalStreamKey(
        string SystemName,
        FneTrafficProtocol Protocol,
        uint StreamId)
    {
        public bool Equals(PhysicalStreamKey other)
            => Protocol == other.Protocol &&
               StreamId == other.StreamId &&
               SystemName.Equals(other.SystemName, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode()
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(SystemName),
                Protocol,
                StreamId);
    }

    private readonly record struct ReceiveCallIdentity(
        string SystemName,
        FneTrafficProtocol Protocol,
        uint SourceId,
        uint DestinationId,
        byte? Slot,
        string CallType)
    {
        public static ReceiveCallIdentity Create(string systemName, FneTrafficFrame traffic)
            => new(
                systemName,
                traffic.Protocol,
                traffic.SourceId,
                traffic.DestinationId,
                traffic.Slot,
                traffic.CallType.Trim().ToUpperInvariant());

        public bool Equals(ReceiveCallIdentity other)
            => Protocol == other.Protocol &&
               SourceId == other.SourceId &&
               DestinationId == other.DestinationId &&
               Slot == other.Slot &&
               SystemName.Equals(other.SystemName, StringComparison.OrdinalIgnoreCase) &&
               CallType.Equals(other.CallType, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode()
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(SystemName),
                Protocol,
                SourceId,
                DestinationId,
                Slot,
                StringComparer.OrdinalIgnoreCase.GetHashCode(CallType));
    }

    private readonly record struct EpisodeStreamUpdate(
        bool Added,
        uint? RemovedStreamId);

    private sealed class EpisodeState(
        long episodeId,
        ReceiveCallIdentity identity,
        uint primaryStreamId,
        DateTimeOffset startedAt,
        EncryptionSnapshot encryption)
    {
        private readonly TrafficEncryptionObservationState encryptionState = new(encryption);
        private readonly Queue<uint> replacementStreamOrder = [];

        public long EpisodeId { get; } = episodeId;
        public ReceiveCallIdentity Identity { get; } = identity;
        public uint PrimaryStreamId { get; } = primaryStreamId;
        public HashSet<uint> StreamIds { get; } = [primaryStreamId];
        public DateTimeOffset StartedAt { get; } = startedAt;
        public DateTimeOffset LastActivityAt { get; set; } = startedAt;
        public DateTimeOffset? LastPhysicalEndAt { get; set; }
        public ReceivePhysicalEndReason? LastPhysicalEndReason { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public DateTimeOffset? MappingExpiresAt { get; set; }
        public EncryptionSnapshot Encryption => encryptionState.Encryption;

        public EpisodeStreamUpdate TrackStream(uint streamId, int maximumStreams)
        {
            if (StreamIds.Contains(streamId))
                return default;

            uint? removedStreamId = null;
            if (StreamIds.Count >= maximumStreams)
            {
                removedStreamId = replacementStreamOrder.Dequeue();
                StreamIds.Remove(removedStreamId.Value);
            }

            StreamIds.Add(streamId);
            replacementStreamOrder.Enqueue(streamId);
            return new EpisodeStreamUpdate(Added: true, removedStreamId);
        }

        public bool IsEncryptionCompatible(EncryptionSnapshot candidate)
        {
            if (!Encryption.IsKnown)
                return true;
            if (candidate.IsKnown)
                return Encryption.HasSameMetadata(candidate);

            // Unknown metadata may extend a known episode only while its
            // current physical stream is still active. After a physical end,
            // a replacement stream must establish its own security state so a
            // new clear call cannot inherit the preceding secure call.
            return LastPhysicalEndAt is null;
        }

        public void ObserveEncryption(FneTrafficFrame traffic)
            => encryptionState.Observe(traffic);

        public void ObservePhysicalEnd(DateTimeOffset endedAt, ReceivePhysicalEndReason reason)
        {
            if (LastPhysicalEndAt is null || endedAt > LastPhysicalEndAt)
            {
                LastPhysicalEndAt = endedAt;
                LastPhysicalEndReason = reason;
                return;
            }
            if (endedAt == LastPhysicalEndAt &&
                reason > LastPhysicalEndReason.GetValueOrDefault())
            {
                LastPhysicalEndReason = reason;
            }
        }

        public ReceiveCallEpisodeSnapshot Snapshot()
            => new(
                EpisodeId,
                Identity.SystemName,
                Identity.Protocol,
                Identity.SourceId,
                Identity.DestinationId,
                Identity.Slot,
                Identity.CallType,
                PrimaryStreamId,
                StreamIds
                    .OrderBy(streamId => streamId == PrimaryStreamId ? 0 : 1)
                    .ThenBy(streamId => streamId)
                    .ToArray(),
                StartedAt,
                LastActivityAt,
                LastPhysicalEndAt is DateTimeOffset physicalEnd && physicalEnd > LastActivityAt
                    ? physicalEnd
                    : LastActivityAt,
                Encryption);
    }
}
