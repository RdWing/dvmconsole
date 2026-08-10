// SPDX-License-Identifier: AGPL-3.0-only
/**
* Digital Voice Modem - Desktop Dispatch Console
* AGPLv3 Open Source. Use is subject to license terms.
* DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
*
* @package DVM / Desktop Dispatch Console
* @license AGPLv3 License (https://opensource.org/licenses/AGPL-3.0)
*
*   Copyright (C) 2026 C. Lovell, Dev_Ranger
*
*/

namespace dvmconsole
{
    /// <summary>
    /// Headless Core patch engine (call-lifecycle slice): membership
    /// normalization, one-way source eligibility, call-start forwarding with
    /// successful-forward tracking, the active-forward-target query
    /// (<see cref="IsForwardTargetActive"/>), active-source audio fan-out,
    /// idempotent
    /// matching call-end teardown, and stale-source failover on a new call
    /// start (a live source is replaced only after it has been quiet past
    /// <see cref="StaleSourceWindow"/>, using the injected clock
    /// <see cref="_utcNow"/>), and periodic stale-source cleanup via
    /// <see cref="CleanupStaleSources"/>. Observed audio refreshes the
    /// live source's activity stamp (see <see cref="HandleAudio"/>).
    /// Source-id passthrough (<see cref="SetSourceIdPassthrough"/>) stamps
    /// new forwards with the inbound source id, and the first non-zero
    /// audio-time source id is latched onto an active forward when
    /// passthrough is enabled (see <see cref="HandleAudio"/>). Audio-time
    /// acquisition begins any target whose start was deferred for lack of a
    /// latched source id as soon as the id arrives. Active patched-stream
    /// suppression is implemented: inbound traffic matching an accepted
    /// outbound forward returns early from <see cref="HandleCallStart"/>
    /// and <see cref="HandleAudio"/> (see <see cref="IsPatchedTransmitStream"/>),
    /// and recently ended forwards stay suppressed for
    /// <see cref="LatePacketSuppressWindow"/> after teardown so late
    /// packets of an ended call cannot restart the patch.
    /// Later slices add audio-time source acquisition (a group with no
    /// active source started by audio).
    /// </summary>
    public sealed class PatchManager
    {
        /// <summary>The currently live inbound source of a group's call.</summary>
        private readonly struct ActiveSource
        {
            public ActiveSource(string systemName, string tgid, uint streamId, DateTime lastActivityUtc, uint sourceId, bool sourceIdLatched)
            {
                SystemName = systemName;
                Tgid = tgid;
                StreamId = streamId;
                LastActivityUtc = lastActivityUtc;
                SourceId = sourceId;
                SourceIdLatched = sourceIdLatched;
            }

            public string SystemName { get; }
            public string Tgid { get; }
            public uint StreamId { get; }

            /// <summary>
            /// The clock value stamped when this source was installed, and
            /// refreshed by <see cref="HandleAudio"/> whenever matching
            /// inbound audio is observed. Staleness is measured against
            /// this, matching the oracle's <c>LastActivityUtc</c>.
            /// </summary>
            public DateTime LastActivityUtc { get; }

            /// <summary>
            /// The source id stamped on this source: the inbound source id
            /// captured at call start, replaced once by the first non-zero
            /// audio-time source id when passthrough is enabled (see
            /// <see cref="HandleAudio"/>). Matching the oracle's
            /// <c>ActiveSource.SourceId</c>.
            /// </summary>
            public uint SourceId { get; }

            /// <summary>
            /// Whether <see cref="SourceId"/> is final. Installed as
            /// <c>sourceId != 0</c> at call start, so a zero inbound id
            /// starts unlatched and latches the first non-zero audio id
            /// exactly once per active source stream. Matching the oracle's
            /// <c>ActiveSource.SourceIdLatched</c>.
            /// </summary>
            public bool SourceIdLatched { get; }
        }

        /// <summary>A successfully begun outbound forward for one target member.</summary>
        private readonly struct ForwardTarget
        {
            public ForwardTarget(string systemName, string tgid, uint streamId, uint outboundSourceId)
            {
                SystemName = systemName;
                Tgid = tgid;
                StreamId = streamId;
                OutboundSourceId = outboundSourceId;
            }

            public string SystemName { get; }
            public string Tgid { get; }
            public uint StreamId { get; }
            public uint OutboundSourceId { get; }
        }

        /// <summary>Deferred begin work for one forwarded member.</summary>
        private readonly struct StartWorkItem
        {
            public StartWorkItem(string groupKey, PatchTalkgroupMember target, string sourceSystemName, string sourceTgid, uint sourceStreamId, uint sourceId, bool passthrough)
            {
                GroupKey = groupKey;
                Target = target;
                SourceSystemName = sourceSystemName;
                SourceTgid = sourceTgid;
                SourceStreamId = sourceStreamId;
                SourceId = sourceId;
                Passthrough = passthrough;
            }

            public string GroupKey { get; }
            public PatchTalkgroupMember Target { get; }
            public string SourceSystemName { get; }
            public string SourceTgid { get; }
            public uint SourceStreamId { get; }

            /// <summary>The inbound source id captured at call start.</summary>
            public uint SourceId { get; }

            /// <summary>
            /// The source-id passthrough setting captured at call start,
            /// so a setting change mid-call never affects deferred starts
            /// of this call (matching the oracle's per-call capture).
            /// </summary>
            public bool Passthrough { get; }
        }

        /// <summary>Deferred end work for one tracked forward.</summary>
        private readonly struct StopWorkItem
        {
            public StopWorkItem(string systemName, string tgid, uint streamId, uint sourceId)
            {
                SystemName = systemName;
                Tgid = tgid;
                StreamId = streamId;
                SourceId = sourceId;
            }

            public string SystemName { get; }
            public string Tgid { get; }
            public uint StreamId { get; }
            public uint SourceId { get; }
        }

        /// <summary>Deferred audio fan-out for one tracked forward.</summary>
        private readonly struct AudioWorkItem
        {
            public AudioWorkItem(string systemName, string tgid, uint sourceId)
            {
                SystemName = systemName;
                Tgid = tgid;
                SourceId = sourceId;
            }

            public string SystemName { get; }
            public string Tgid { get; }
            public uint SourceId { get; }
        }

        private readonly Func<string, string, uint, uint> _beginForward;
        private readonly Action<string, string, uint, uint> _endForward;
        private readonly Action<string, string, byte[], uint> _sendForwardAudio;
        private readonly Func<string, string, uint> _getFallbackSourceId;
        private readonly Func<DateTime> _utcNow;

        /// <summary>
        /// When true, a new forward uses the inbound call's source id
        /// instead of the per-target fallback resolver. Captured per call
        /// start, so a change never retroactively affects an in-flight
        /// call's deferred starts (matching the oracle's
        /// <c>sourceIdPassthrough</c> field and per-call capture).
        /// </summary>
        private bool _sourceIdPassthrough = false;

        /// <summary>
        /// A live source with no observed activity longer than this window
        /// is considered stale (a missed call end), matching the oracle's
        /// <c>LatePacketSuppressWindow</c>. The comparison is strictly
        /// greater-than, so a source is stale only past the full window.
        /// </summary>
        private static readonly TimeSpan StaleSourceWindow = TimeSpan.FromMilliseconds(2000);

        /// <summary>
        /// How long a recently ended outbound stream stays suppressed as a
        /// potential inbound source after its forward was torn down, so
        /// late-arriving packets of the ended call cannot restart a patch
        /// on the same stream (matching the oracle's
        /// <c>LatePacketSuppressWindow</c>, dvmconsole/PatchManager.cs:127).
        /// </summary>
        private static readonly TimeSpan LatePacketSuppressWindow = TimeSpan.FromMilliseconds(2000);

        /// <summary>
        /// Recently ended outbound stream keys (normalized
        /// system|tgid|stream, see <see cref="BuildStreamKey"/>) whose
        /// suppression entry has not yet expired, keyed by the stream key
        /// with the UTC expiry stamp. Matches the oracle's
        /// <c>recentlyEndedOutboundStreams</c> dictionary: entries are
        /// pruned by <see cref="IsPatchedTransmitStream"/> once the
        /// injected clock passes the expiry, and an accepted new forward
        /// on the same stream key removes its entry (see
        /// <see cref="ExecuteStarts"/>).
        /// </summary>
        private readonly Dictionary<string, DateTime> _recentlyEndedOutboundStreams =
            new Dictionary<string, DateTime>();

        /// <summary>
        /// Normalized groups, keyed by the raw caller-supplied group key.
        /// Each member list is deduplicated, trimmed, and ordered as the first
        /// normalized spelling of each identity appeared in the input.
        /// </summary>
        private readonly Dictionary<string, List<PatchTalkgroupMember>> _groups =
            new Dictionary<string, List<PatchTalkgroupMember>>();

        /// <summary>
        /// One-way mode per normalized group, keyed by the raw caller-supplied
        /// group key. A missing entry means bidirectional (one-way = false),
        /// matching the WPF oracle's TryGetValue default.
        /// </summary>
        private readonly Dictionary<string, bool> _oneWayModes =
            new Dictionary<string, bool>();

        /// <summary>
        /// Live call source per group, keyed by the raw caller-supplied group
        /// key. Present while a call is being forwarded for that group.
        /// </summary>
        private readonly Dictionary<string, ActiveSource> _activeSources =
            new Dictionary<string, ActiveSource>();

        /// <summary>
        /// Successfully begun forwards per group, keyed by the raw
        /// caller-supplied group key. Targets are tracked in membership
        /// order; a group's list exists exactly while its
        /// <see cref="_activeSources"/> entry exists.
        /// </summary>
        private readonly Dictionary<string, List<ForwardTarget>> _activeTargets =
            new Dictionary<string, List<ForwardTarget>>();

        /// <param name="beginForward">
        /// Invoked once per forwarded member when a call starts on a patch
        /// member. Returns the stream id of the forwarded call.
        /// </param>
        /// <param name="endForward">Invoked when a forwarded call ends.</param>
        /// <param name="sendForwardAudio">Invoked for forwarded audio.</param>
        /// <param name="getFallbackSourceId">
        /// Resolves the source id stamped on forwarded calls when
        /// source-id passthrough is disabled or the inbound source id is 0.
        /// </param>
        /// <param name="utcNow">Clock seam; defaults to <see cref="DateTime.UtcNow"/>.</param>
        public PatchManager(
            Func<string, string, uint, uint> beginForward,
            Action<string, string, uint, uint> endForward,
            Action<string, string, byte[], uint> sendForwardAudio,
            Func<string, string, uint> getFallbackSourceId,
            Func<DateTime> utcNow = null)
        {
            _beginForward = beginForward ?? throw new ArgumentNullException(nameof(beginForward));
            _endForward = endForward ?? throw new ArgumentNullException(nameof(endForward));
            _sendForwardAudio = sendForwardAudio ?? throw new ArgumentNullException(nameof(sendForwardAudio));
            _getFallbackSourceId = getFallbackSourceId ?? throw new ArgumentNullException(nameof(getFallbackSourceId));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>
        /// Enables or disables source-id passthrough for new forwarded
        /// starts. When enabled, a call started afterwards forwards its
        /// inbound source id to every target; when disabled (or the
        /// inbound source id is 0), the per-target fallback resolver is
        /// used. A call already in progress is unaffected, matching the
        /// oracle's per-call capture of <c>sourceIdPassthrough</c>.
        /// </summary>
        /// <param name="enabled">Whether passthrough is enabled.</param>
        public void SetSourceIdPassthrough(bool enabled)
        {
            _sourceIdPassthrough = enabled;
        }

        /// <summary>
        /// Replaces the configured patch groups, with no group forced into
        /// one-way mode. Equivalent to calling the overload with a null
        /// one-way-mode map.
        /// </summary>
        public void ApplyMemberships(Dictionary<string, List<PatchTalkgroupMember>> memberships)
        {
            ApplyMemberships(memberships, null);
        }

        /// <summary>
        /// Replaces the configured patch groups and their one-way modes.
        /// Members are normalized: null/blank members (or blank
        /// SystemName/Tgid) are dropped, values are trimmed, identities are
        /// deduplicated (system name compared case-insensitively, tgid
        /// ordinally) retaining the first normalized spelling, and list order
        /// is preserved. Groups whose normalized member list is empty are
        /// dropped entirely. One-way modes are looked up per group key
        /// case-insensitively; an absent key means bidirectional.
        /// <para>
        /// Replacement is transactional: if the incoming groups and one-way
        /// modes are semantically identical to the current configuration, the
        /// active call state is left untouched. Otherwise every group that
        /// was removed or changed (membership or mode) has its tracked active
        /// forwards ended once, with the stream/source values stored at begin
        /// time, before the new configuration is installed. A null
        /// <paramref name="memberships"/> means an empty incoming
        /// configuration and tears down every active old group.
        /// </para>
        /// </summary>
        public void ApplyMemberships(
            Dictionary<string, List<PatchTalkgroupMember>> memberships,
            Dictionary<string, bool> oneWayModes)
        {
            Dictionary<string, GroupMembership> incoming = NormalizeMemberships(memberships, oneWayModes);

            List<StopWorkItem> stops = new List<StopWorkItem>();
            if (MembershipsEqual(incoming))
                return;

            List<string> keysToRemove = new List<string>();
            foreach (string key in _groups.Keys)
            {
                if (!incoming.ContainsKey(key) ||
                    !MembersEqual(_groups[key], incoming[key].Members) ||
                    GetOneWay(key) != incoming[key].OneWay)
                {
                    keysToRemove.Add(key);
                }
            }

            foreach (string key in keysToRemove)
            {
                if (_activeTargets.TryGetValue(key, out List<ForwardTarget> targets))
                {
                    foreach (ForwardTarget target in targets)
                    {
                        stops.Add(new StopWorkItem(target.SystemName, target.Tgid, target.StreamId, target.OutboundSourceId));
                        RecordRecentlyEndedOutboundStream(target);
                    }
                    _activeTargets.Remove(key);
                }
                _activeSources.Remove(key);
                _groups.Remove(key);
                _oneWayModes.Remove(key);
            }

            foreach (KeyValuePair<string, GroupMembership> kvp in incoming)
            {
                if (_groups.ContainsKey(kvp.Key))
                    continue;

                _groups[kvp.Key] = kvp.Value.Members;
                _oneWayModes[kvp.Key] = kvp.Value.OneWay;
            }

            foreach (StopWorkItem stop in stops)
                _endForward(stop.SystemName, stop.Tgid, stop.StreamId, stop.SourceId);
        }

        /// <summary>
        /// For every configured group for which the normalized call source
        /// identity is an eligible source, records the source as active and
        /// invokes <see cref="_beginForward"/> once per other normalized
        /// member, in membership order, with the per-target fallback source
        /// id. Forwards that return a non-zero stream id are tracked so a
        /// matching <see cref="HandleCallEnd"/> can end them exactly once.
        /// A group is bidirectional unless its one-way mode is set, in which
        /// case only the first normalized member may act as source. The
        /// source member itself is never forwarded to. Repeating the same
        /// source stream never duplicates forwards. A different source never
        /// replaces a live one until the live source is stale (no activity
        /// for strictly more than <see cref="StaleSourceWindow"/>, measured
        /// with <see cref="_utcNow"/>), at which point the old forwards are
        /// ended once, in tracked membership order with their stored
        /// stream/source ids, before the new source's forwards begin.
        /// </summary>
        public void HandleCallStart(string systemName, string tgid, uint streamId, uint sourceId)
        {
            string callSystem = systemName?.Trim() ?? string.Empty;
            string callTgid = tgid?.Trim() ?? string.Empty;
            if (callSystem.Length == 0 || callTgid.Length == 0)
                return;

            // Inbound traffic that is itself an active patched transmit
            // stream is suppressed before any source matching, clock
            // reads, latching, or sends (matching the oracle's early
            // return, dvmconsole/PatchManager.cs:223).
            if (IsPatchedTransmitStream(callSystem, callTgid, streamId))
                return;

            DateTime now = _utcNow();
            bool passthrough = _sourceIdPassthrough;

            List<StartWorkItem> starts = new List<StartWorkItem>();
            List<StopWorkItem> stops = new List<StopWorkItem>();
            foreach (KeyValuePair<string, List<PatchTalkgroupMember>> group in _groups)
            {
                List<PatchTalkgroupMember> members = group.Value;
                int sourceIndex = -1;
                for (int i = 0; i < members.Count; i++)
                {
                    if (IsSameIdentity(members[i].SystemName, members[i].Tgid, callSystem, callTgid))
                    {
                        sourceIndex = i;
                        break;
                    }
                }
                if (sourceIndex < 0)
                    continue;

                if (_oneWayModes.TryGetValue(group.Key, out bool oneWay) && oneWay && sourceIndex != 0)
                    continue;

                if (_activeSources.TryGetValue(group.Key, out ActiveSource activeSource))
                {
                    // Repeating the live source/stream never duplicates
                    // forwards, even once the source is stale.
                    if (IsSameIdentity(activeSource.SystemName, activeSource.Tgid, callSystem, callTgid) &&
                        activeSource.StreamId == streamId)
                        continue;

                    // A live source is never replaced until it goes stale:
                    // if call-end was missed and the previous source has
                    // been quiet past the stale window, fail over to the
                    // new inbound source by tearing down the old forwards.
                    if (!IsSourceStale(activeSource, now))
                        continue;

                    if (_activeTargets.TryGetValue(group.Key, out List<ForwardTarget> oldTargets))
                    {
                        foreach (ForwardTarget target in oldTargets)
                        {
                            stops.Add(new StopWorkItem(target.SystemName, target.Tgid, target.StreamId, target.OutboundSourceId));
                            RecordRecentlyEndedOutboundStream(target);
                        }
                        _activeTargets.Remove(group.Key);
                    }
                    _activeSources.Remove(group.Key);
                }

                PatchTalkgroupMember source = members[sourceIndex];
                _activeSources[group.Key] = new ActiveSource(
                    source.SystemName, source.Tgid, streamId, now, sourceId, sourceId != 0);
                _activeTargets[group.Key] = new List<ForwardTarget>();

                for (int i = 0; i < members.Count; i++)
                {
                    if (i == sourceIndex)
                        continue;

                    starts.Add(new StartWorkItem(group.Key, members[i], source.SystemName, source.Tgid, streamId, sourceId, passthrough));
                }
            }

            // Tear down stale forwards before beginning the new ones,
            // matching the oracle's ExecuteStops-then-ExecuteStarts order.
            foreach (StopWorkItem stop in stops)
                _endForward(stop.SystemName, stop.Tgid, stop.StreamId, stop.SourceId);

            ExecuteStarts(starts);
        }

        /// <summary>
        /// Executes deferred begin work in queue order, resolving each
        /// target's forwarded source id exactly as the oracle does: the
        /// inbound source id captured on the work item when passthrough was
        /// enabled at capture time and the id is non-zero, else the
        /// per-target fallback resolver. A begin that returns a zero stream
        /// id is discarded. A successful begin is tracked only while the
        /// source that requested it is still live on the same stream and the
        /// target is not already tracked; otherwise the stream is closed
        /// immediately (the oracle's deferred-acceptance reconciliation).
        /// </summary>
        private void ExecuteStarts(List<StartWorkItem> starts)
        {
            foreach (StartWorkItem start in starts)
            {
                uint forwardedSourceId;
                if (start.Passthrough && start.SourceId != 0)
                    forwardedSourceId = start.SourceId;
                else
                    forwardedSourceId = _getFallbackSourceId(start.Target.SystemName, start.Target.Tgid);

                uint outboundStreamId = _beginForward(start.Target.SystemName, start.Target.Tgid, forwardedSourceId);
                if (outboundStreamId == 0)
                    continue;

                // Track only while the source that requested this start is
                // still live and the target is not already tracked; otherwise
                // close the stream immediately, mirroring the oracle's
                // deferred-acceptance reconciliation.
                if (_groups.TryGetValue(start.GroupKey, out List<PatchTalkgroupMember> members) &&
                    _activeSources.TryGetValue(start.GroupKey, out ActiveSource source) &&
                    source.StreamId == start.SourceStreamId &&
                    IsSameIdentity(source.SystemName, source.Tgid, start.SourceSystemName, start.SourceTgid) &&
                    !ContainsTarget(_activeTargets[start.GroupKey], start.Target))
                {
                    _activeTargets[start.GroupKey].Add(new ForwardTarget(
                        start.Target.SystemName, start.Target.Tgid, outboundStreamId, forwardedSourceId));

                    // An accepted forward reuses its stream key, so any
                    // recently-ended suppression entry for that key is
                    // dropped: the stream is live again and suppression is
                    // covered by the active-target scan (matching the
                    // oracle's ExecuteStarts,
                    // dvmconsole/PatchManager.cs:455). Rejected zero
                    // begins never reach this point and add no entries.
                    _recentlyEndedOutboundStreams.Remove(
                        BuildStreamKey(start.Target.SystemName, start.Target.Tgid, outboundStreamId));
                }
                else
                {
                    _endForward(start.Target.SystemName, start.Target.Tgid, outboundStreamId, forwardedSourceId);
                }
            }
        }

        /// <summary>
        /// Fans inbound PCM out to every successfully begun forward of every
        /// group whose live source matches the normalized call identity and
        /// inbound stream id. The matching live source's
        /// <see cref="ActiveSource.LastActivityUtc"/> is first refreshed to
        /// the current <see cref="_utcNow"/> value, so staleness is measured
        /// from the latest observed audio (matching the oracle's
        /// <c>group.Source.LastActivityUtc = now</c>). When source-id
        /// passthrough is enabled and the active source has not yet latched
        /// a source id, the first non-zero inbound audio source id is
        /// latched onto the active source and every tracked target exactly
        /// once per active source stream (matching the oracle's
        /// <c>SourceIdLatched</c> latch block), before sends are collected;
        /// later audio ids never change a latched id, and a disabled
        /// passthrough never latches. Once the source id is latched (or
        /// immediately when passthrough is disabled), any member other than
        /// the source that is not yet tracked is begun with the source's
        /// current (latched) id, so a target whose start was deferred at
        /// call start for lack of a passthrough id is acquired as soon as
        /// the id arrives (matching the oracle's missing-target acquisition,
        /// dvmconsole/PatchManager.cs:322-330); deferred begins execute
        /// before sends, so the packet that triggers an acquisition is not
        /// itself forwarded to the newly begun target. Each tracked target
        /// receives
        /// <see cref="_sendForwardAudio"/> exactly once, in tracked
        /// membership order, with the pcm buffer passed through unchanged
        /// and the per-target outbound source id stored at begin time (the
        /// fallback resolver is not consulted again). A non-matching
        /// source/stream, a group with no active source, and a group whose
        /// live call was torn down are all no-ops (and do not refresh the
        /// stamp). Starts and sends are deferred until all matching targets
        /// have been collected, so callback exceptions propagate without
        /// mutating manager state mid-fan-out.
        /// </summary>
        public void HandleAudio(string systemName, string tgid, uint streamId, uint sourceId, byte[] pcm)
        {
            string callSystem = systemName?.Trim() ?? string.Empty;
            string callTgid = tgid?.Trim() ?? string.Empty;
            if (callSystem.Length == 0 || callTgid.Length == 0)
                return;

            // Inbound traffic that is itself an active patched transmit
            // stream is suppressed before any source matching, clock
            // reads, latching, or sends (matching the oracle's early
            // return, dvmconsole/PatchManager.cs:275).
            if (IsPatchedTransmitStream(callSystem, callTgid, streamId))
                return;

            DateTime now = _utcNow();
            bool passthrough = _sourceIdPassthrough;

            List<StartWorkItem> starts = new List<StartWorkItem>();
            List<AudioWorkItem> sends = new List<AudioWorkItem>();
            foreach (KeyValuePair<string, List<PatchTalkgroupMember>> group in _groups)
            {
                if (!_activeSources.TryGetValue(group.Key, out ActiveSource source))
                    continue;
                if (!IsSameIdentity(source.SystemName, source.Tgid, callSystem, callTgid) || source.StreamId != streamId)
                    continue;

                // The ActiveSource value is immutable, so refreshing the
                // activity stamp (and latching, when applicable) replaces
                // it with an equivalent source carrying the new state.
                // State is updated before sends are collected, matching
                // the oracle.
                bool latch = passthrough && !source.SourceIdLatched && sourceId != 0;
                ActiveSource updatedSource = new ActiveSource(
                    source.SystemName, source.Tgid, source.StreamId, now,
                    latch ? sourceId : source.SourceId,
                    source.SourceIdLatched || latch);
                _activeSources[group.Key] = updatedSource;

                if (latch)
                {
                    // The first non-zero audio source id replaces the
                    // outbound id of every tracked forward of this source
                    // (matching the oracle's per-target update), so the
                    // sends collected below carry the latched id.
                    List<ForwardTarget> targets = _activeTargets[group.Key];
                    for (int i = 0; i < targets.Count; i++)
                    {
                        ForwardTarget target = targets[i];
                        targets[i] = new ForwardTarget(target.SystemName, target.Tgid, target.StreamId, sourceId);
                    }
                }

                // If starts were deferred because the passthrough source id
                // was unavailable at call start, begin any missing targets
                // as soon as the id is latched, or immediately when
                // passthrough is disabled (matching the oracle's
                // missing-target acquisition). The latched id is stamped on
                // the forwarded start; already-tracked targets are never
                // begun again.
                if (!passthrough || updatedSource.SourceIdLatched)
                {
                    List<PatchTalkgroupMember> members = group.Value;
                    for (int i = 0; i < members.Count; i++)
                    {
                        PatchTalkgroupMember member = members[i];
                        if (IsSameIdentity(member.SystemName, member.Tgid, callSystem, callTgid))
                            continue;
                        if (ContainsTarget(_activeTargets[group.Key], member))
                            continue;

                        starts.Add(new StartWorkItem(
                            group.Key, member,
                            updatedSource.SystemName, updatedSource.Tgid, updatedSource.StreamId,
                            updatedSource.SourceId, passthrough));
                    }
                }

                foreach (ForwardTarget target in _activeTargets[group.Key])
                    sends.Add(new AudioWorkItem(target.SystemName, target.Tgid, target.OutboundSourceId));
            }

            // Deferred starts execute before sends are dispatched, so a
            // target acquired by this packet begins receiving subsequent
            // audio, not the packet that triggered its start (matching the
            // oracle's ExecuteStarts-then-send order).
            ExecuteStarts(starts);

            foreach (AudioWorkItem send in sends)
                _sendForwardAudio(send.SystemName, send.Tgid, pcm, send.SourceId);
        }

        /// <summary>
        /// Ends every tracked forward of every group whose live source
        /// matches the normalized call identity and inbound stream id, then
        /// clears the group's active source and targets. Each tracked target
        /// is ended once, in membership order, with the stream id returned by
        /// its <see cref="_beginForward"/> and the fallback source id used
        /// for it. A non-matching or repeated end is a no-op.
        /// </summary>
        public void HandleCallEnd(string systemName, string tgid, uint streamId)
        {
            string callSystem = systemName?.Trim() ?? string.Empty;
            string callTgid = tgid?.Trim() ?? string.Empty;
            if (callSystem.Length == 0 || callTgid.Length == 0)
                return;

            List<StopWorkItem> stops = new List<StopWorkItem>();
            foreach (KeyValuePair<string, List<PatchTalkgroupMember>> group in _groups)
            {
                if (!_activeSources.TryGetValue(group.Key, out ActiveSource source))
                    continue;
                if (!IsSameIdentity(source.SystemName, source.Tgid, callSystem, callTgid) || source.StreamId != streamId)
                    continue;

                foreach (ForwardTarget target in _activeTargets[group.Key])
                {
                    stops.Add(new StopWorkItem(target.SystemName, target.Tgid, target.StreamId, target.OutboundSourceId));
                    RecordRecentlyEndedOutboundStream(target);
                }

                _activeSources.Remove(group.Key);
                _activeTargets.Remove(group.Key);
            }

            foreach (StopWorkItem stop in stops)
                _endForward(stop.SystemName, stop.Tgid, stop.StreamId, stop.SourceId);
        }

        /// <summary>
        /// Determines whether a member is currently a tracked forwarded
        /// destination: a forward accepted at call start (or acquired by
        /// missing-target acquisition) that has not yet been torn down by
        /// <see cref="HandleCallEnd"/>, membership replacement, or
        /// <see cref="CleanupStaleSources"/>. The identity is normalized
        /// like every other inbound identity — system name trimmed and
        /// compared case-insensitively, talkgroup id trimmed and compared
        /// ordinally — matching the oracle's <c>BuildKey</c> key and the
        /// membership identity rule. A blank identity returns false (the
        /// oracle's tracked set can never contain a blank key either, so
        /// this is an explicit guard, not a divergence). The active source
        /// member, unknown members, and members whose begin was rejected
        /// (zero outbound stream id) are never active targets.
        /// </summary>
        /// <param name="systemName">The member's system name.</param>
        /// <param name="tgid">The member's talkgroup id.</param>
        /// <returns>
        /// True if the member is currently being forwarded to as a patch
        /// destination; otherwise false.
        /// </returns>
        public bool IsForwardTargetActive(string systemName, string tgid)
        {
            string querySystem = systemName?.Trim() ?? string.Empty;
            string queryTgid = tgid?.Trim() ?? string.Empty;
            if (querySystem.Length == 0 || queryTgid.Length == 0)
                return false;

            foreach (KeyValuePair<string, List<ForwardTarget>> group in _activeTargets)
            {
                foreach (ForwardTarget target in group.Value)
                {
                    if (IsSameIdentity(target.SystemName, target.Tgid, querySystem, queryTgid))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Determines whether the given identity and stream id identify an
        /// outbound stream this manager is currently forwarding: a forward
        /// accepted at call start (or acquired by missing-target
        /// acquisition) whose target identity matches — system name trimmed
        /// and compared case-insensitively, talkgroup id trimmed and
        /// compared ordinally, matching <see cref="IsSameIdentity"/> — and
        /// whose outbound stream id equals <paramref name="streamId"/>.
        /// A forward torn down by any teardown path stays suppressed for
        /// <see cref="LatePacketSuppressWindow"/> after its end, so
        /// late-arriving traffic of the ended call is still recognized
        /// until the injected clock passes the recorded expiry (expired
        /// entries are pruned with one <see cref="_utcNow"/> read per
        /// query). Inbound traffic that is itself a patched transmit
        /// stream is suppressed by the early returns in
        /// <see cref="HandleCallStart"/> and <see cref="HandleAudio"/>,
        /// preventing a patch from re-forwarding its own outbound traffic.
        /// Matching the oracle's <c>outboundStreams</c> membership check
        /// and <c>recentlyEndedOutboundStreams</c> window
        /// (dvmconsole/PatchManager.cs:369-377). A blank identity returns
        /// false (the oracle's stream-key set can never contain a blank
        /// key either, so this is an explicit guard, not a divergence).
        /// </summary>
        /// <param name="systemName">The system name to query.</param>
        /// <param name="tgid">The talkgroup id to query.</param>
        /// <param name="streamId">The outbound stream id to query.</param>
        /// <returns>
        /// True if the identity and stream id identify a currently active
        /// or recently ended (within <see cref="LatePacketSuppressWindow"/>)
        /// patched transmit stream; otherwise false.
        /// </returns>
        public bool IsPatchedTransmitStream(string systemName, string tgid, uint streamId)
        {
            string querySystem = systemName?.Trim() ?? string.Empty;
            string queryTgid = tgid?.Trim() ?? string.Empty;
            if (querySystem.Length == 0 || queryTgid.Length == 0)
                return false;

            // Prune expired recently-ended entries with a single injected
            // clock read per query, matching the oracle's
            // CleanupExpiredRecentlyEndedUnsafe
            // (dvmconsole/PatchManager.cs:607-619): an entry whose expiry
            // has passed (expiry &lt;= now) is removed.
            DateTime now = _utcNow();
            if (_recentlyEndedOutboundStreams.Count > 0)
            {
                List<string> expired = new List<string>();
                foreach (KeyValuePair<string, DateTime> kvp in _recentlyEndedOutboundStreams)
                {
                    if (kvp.Value <= now)
                        expired.Add(kvp.Key);
                }
                foreach (string key in expired)
                    _recentlyEndedOutboundStreams.Remove(key);
            }

            foreach (KeyValuePair<string, List<ForwardTarget>> group in _activeTargets)
            {
                foreach (ForwardTarget target in group.Value)
                {
                    if (IsSameIdentity(target.SystemName, target.Tgid, querySystem, queryTgid) &&
                        target.StreamId == streamId)
                    {
                        return true;
                    }
                }
            }

            // An unexpired recently ended forward of the same normalized
            // stream key is still suppressed (matching the oracle's
            // recentlyEndedOutboundStreams membership check).
            return _recentlyEndedOutboundStreams.ContainsKey(BuildStreamKey(querySystem, queryTgid, streamId));
        }

        /// <summary>
        /// Ends every tracked forward of every group whose active source has
        /// been quiet for strictly longer than <see cref="StaleSourceWindow"/>
        /// (a missed call end), then clears those sources. Each affected
        /// group's targets are ended once, in tracked membership order, with
        /// the stream id returned by their <see cref="_beginForward"/> and
        /// the fallback source id used for them. Ends are deferred until all
        /// stale groups have been collected and removed, so callback
        /// exceptions propagate without mutating manager state mid-teardown.
        /// Returns the number of stale source groups cleaned up; groups with
        /// no active source (or a fresh one) are untouched, and a repeated
        /// call after cleanup is a no-op.
        /// </summary>
        public int CleanupStaleSources()
        {
            DateTime now = _utcNow();

            List<string> staleKeys = new List<string>();
            foreach (KeyValuePair<string, ActiveSource> kvp in _activeSources)
            {
                if (IsSourceStale(kvp.Value, now))
                    staleKeys.Add(kvp.Key);
            }

            List<StopWorkItem> stops = new List<StopWorkItem>();
            int cleanedSources = 0;
            foreach (string key in staleKeys)
            {
                if (_activeTargets.TryGetValue(key, out List<ForwardTarget> targets))
                {
                    foreach (ForwardTarget target in targets)
                    {
                        stops.Add(new StopWorkItem(target.SystemName, target.Tgid, target.StreamId, target.OutboundSourceId));
                        RecordRecentlyEndedOutboundStream(target);
                    }
                    _activeTargets.Remove(key);
                }
                _activeSources.Remove(key);
                cleanedSources++;
            }

            foreach (StopWorkItem stop in stops)
                _endForward(stop.SystemName, stop.Tgid, stop.StreamId, stop.SourceId);

            return cleanedSources;
        }

        private static bool IsSameIdentity(string memberSystemName, string memberTgid, string systemName, string tgid)
        {
            return string.Equals(memberSystemName, systemName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(memberTgid, tgid, StringComparison.Ordinal);
        }

        /// <summary>
        /// Builds the normalized stream key (trimmed system name lowered
        /// case-insensitively, trimmed talkgroup id ordinally, stream id)
        /// used by the recently-ended suppression dictionary, matching the
        /// oracle's <c>BuildStreamKey</c> normalization
        /// (dvmconsole/PatchManager.cs:601-604).
        /// </summary>
        private static string BuildStreamKey(string systemName, string tgid, uint streamId)
        {
            return $"{(systemName ?? string.Empty).Trim().ToLowerInvariant()}|{(tgid ?? string.Empty).Trim()}|{streamId}";
        }

        /// <summary>
        /// Records an ended forward's stream key in the recently-ended
        /// suppression dictionary with expiry
        /// <c>_utcNow() + LatePacketSuppressWindow</c>, matching the
        /// oracle's CollectAndClearStops write
        /// (dvmconsole/PatchManager.cs:486-488) but reading the injected
        /// clock seam rather than <see cref="DateTime.UtcNow"/>.
        /// </summary>
        private void RecordRecentlyEndedOutboundStream(ForwardTarget target)
        {
            _recentlyEndedOutboundStreams[BuildStreamKey(target.SystemName, target.Tgid, target.StreamId)] =
                _utcNow() + LatePacketSuppressWindow;
        }

        /// <summary>
        /// Determines whether an active source has likely gone stale due to
        /// a missed call end: no activity observed for strictly longer than
        /// <see cref="StaleSourceWindow"/>. Matches the oracle's
        /// <c>IsSourceStale</c> comparison.
        /// </summary>
        private static bool IsSourceStale(ActiveSource source, DateTime nowUtc)
        {
            return nowUtc - source.LastActivityUtc > StaleSourceWindow;
        }

        private static bool ContainsTarget(List<ForwardTarget> targets, PatchTalkgroupMember member)
        {
            foreach (ForwardTarget target in targets)
            {
                if (IsSameIdentity(target.SystemName, target.Tgid, member.SystemName, member.Tgid))
                    return true;
            }
            return false;
        }

        /// <summary>A normalized incoming patch group.</summary>
        private sealed class GroupMembership
        {
            public List<PatchTalkgroupMember> Members { get; set; }
            public bool OneWay { get; set; }
        }

        /// <summary>
        /// Normalizes an incoming membership document: trims, drops blank
        /// members, deduplicates by identity retaining the first normalized
        /// spelling, preserves list order, omits groups whose member list is
        /// empty, and resolves each group's one-way mode case-insensitively.
        /// A null <paramref name="memberships"/> normalizes to an empty
        /// configuration.
        /// </summary>
        private static Dictionary<string, GroupMembership> NormalizeMemberships(
            Dictionary<string, List<PatchTalkgroupMember>> memberships,
            Dictionary<string, bool> oneWayModes)
        {
            Dictionary<string, bool> normalizedOneWay = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, bool> kvp in oneWayModes ?? new Dictionary<string, bool>())
                normalizedOneWay[kvp.Key] = kvp.Value;

            Dictionary<string, GroupMembership> normalized = new Dictionary<string, GroupMembership>();
            foreach (KeyValuePair<string, List<PatchTalkgroupMember>> pair in
                memberships ?? new Dictionary<string, List<PatchTalkgroupMember>>())
            {
                if (pair.Key == null || pair.Value == null)
                    continue;

                List<PatchTalkgroupMember> members = new List<PatchTalkgroupMember>();
                Dictionary<string, HashSet<string>> seen =
                    new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

                foreach (PatchTalkgroupMember member in pair.Value)
                {
                    if (member == null)
                        continue;

                    string systemName = member.SystemName?.Trim() ?? string.Empty;
                    string tgid = member.Tgid?.Trim() ?? string.Empty;
                    if (systemName.Length == 0 || tgid.Length == 0)
                        continue;

                    if (!seen.TryGetValue(systemName, out HashSet<string> tgids))
                    {
                        tgids = new HashSet<string>(StringComparer.Ordinal);
                        seen.Add(systemName, tgids);
                    }
                    if (!tgids.Add(tgid))
                        continue;

                    members.Add(new PatchTalkgroupMember
                    {
                        SystemName = systemName,
                        Tgid = tgid
                    });
                }

                if (members.Count == 0)
                    continue;

                normalized[pair.Key] = new GroupMembership
                {
                    Members = members,
                    OneWay = normalizedOneWay.TryGetValue(pair.Key, out bool oneWay) && oneWay
                };
            }

            return normalized;
        }

        /// <summary>
        /// Determines whether the current configuration is semantically
        /// identical to <paramref name="incoming"/>: same group keys with
        /// equal member identity sets and equal one-way modes.
        /// </summary>
        private bool MembershipsEqual(Dictionary<string, GroupMembership> incoming)
        {
            if (_groups.Count != incoming.Count)
                return false;

            foreach (KeyValuePair<string, GroupMembership> kvp in incoming)
            {
                if (!_groups.TryGetValue(kvp.Key, out List<PatchTalkgroupMember> members))
                    return false;
                if (!MembersEqual(members, kvp.Value.Members))
                    return false;
                if (GetOneWay(kvp.Key) != kvp.Value.OneWay)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Determines whether two normalized member lists contain the same
        /// identities (system name compared case-insensitively, tgid
        /// ordinally), regardless of order.
        /// </summary>
        private static bool MembersEqual(List<PatchTalkgroupMember> left, List<PatchTalkgroupMember> right)
        {
            if (left.Count != right.Count)
                return false;

            foreach (PatchTalkgroupMember member in left)
            {
                if (!ContainsMember(right, member))
                    return false;
            }

            return true;
        }

        private static bool ContainsMember(List<PatchTalkgroupMember> members, PatchTalkgroupMember member)
        {
            foreach (PatchTalkgroupMember candidate in members)
            {
                if (IsSameIdentity(candidate.SystemName, candidate.Tgid, member.SystemName, member.Tgid))
                    return true;
            }
            return false;
        }

        private bool GetOneWay(string groupKey)
        {
            return _oneWayModes.TryGetValue(groupKey, out bool oneWay) && oneWay;
        }
    }
}
