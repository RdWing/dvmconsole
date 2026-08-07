// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using DvmConsole.Platform.Audio;

namespace DvmConsole.Avalonia.Services
{
    /// <summary>
    /// Metadata describing one classified receive frame, raised by the
    /// FNE receive glue's <see cref="FneReceiveGlue.CallFrameObserved"/>
    /// seam. Voice frames and terminators both classify; terminators
    /// carry <see cref="IsTerminator"/> so the store can close the
    /// call's stream without recording the terminator itself.
    /// </summary>
    public sealed record ReceivedCallMetadata(
        string SystemName,
        uint SrcId,
        uint DstId,
        byte Slot,
        VoiceMode Mode,
        uint StreamId,
        string Key,
        bool IsTerminator);

    /// <summary>
    /// One immutable call-history row: the codeplug channel name for the
    /// receive target (raw-key fallback when no channel matched), the
    /// system, source/destination ids, the subscriber alias resolved at
    /// record time through the optional alias resolver (empty when none
    /// is set or the id is unmatched), the voice mode and the receive
    /// timestamp. Get-only: rows are replaced wholesale by refresh,
    /// never mutated in place.
    /// </summary>
    public sealed class CallHistoryEntry
    {
        /// <summary>
        /// Creates a call-history row.
        /// </summary>
        public CallHistoryEntry(
            string key,
            string channelName,
            string systemName,
            uint srcId,
            uint dstId,
            string alias,
            VoiceMode mode,
            DateTimeOffset timestamp)
        {
            Key = key;
            ChannelName = channelName;
            SystemName = systemName;
            SrcId = srcId;
            DstId = dstId;
            Alias = alias;
            Mode = mode;
            Timestamp = timestamp;
        }

        /// <summary>The talkgroup router key of the call stream.</summary>
        public string Key { get; }

        /// <summary>The resolved codeplug channel name, or empty for the raw-key fallback.</summary>
        public string ChannelName { get; }

        /// <summary>The FNE system name the frame arrived on.</summary>
        public string SystemName { get; }

        /// <summary>The transmitting radio id.</summary>
        public uint SrcId { get; }

        /// <summary>The destination talkgroup id.</summary>
        public uint DstId { get; }

        /// <summary>The subscriber alias, resolved at record time via the optional alias resolver.</summary>
        public string Alias { get; }

        /// <summary>The voice mode of the call.</summary>
        public VoiceMode Mode { get; }

        /// <summary>When the first frame of the call stream was recorded.</summary>
        public DateTimeOffset Timestamp { get; }
    }

    /// <summary>
    /// Lock-guarded newest-first history of received calls. WPF
    /// CallHistoryWindow parity: the cap is clamped to [5,100] and the
    /// oldest entry is evicted at the cap (WPF :112,150-153,:270-281).
    /// One entry is kept per call stream per key — a frame whose key's
    /// stream id matches the last recorded frame is a dedup no-op, and a
    /// terminator clears the key's stream without being recorded itself,
    /// so a reused stream id after a call end still starts a fresh entry
    /// (WPF isNewCallStream parity MainWindow.DMR.cs:335). The optional
    /// suppression delegate (system name, source id) mirrors the WPF
    /// console-RID filter (MainWindow.Tar.cs:177-180): true means the
    /// frame is not recorded. The optional alias resolver set through
    /// <see cref="SetAliasResolver"/> resolves each recorded entry's
    /// <see cref="CallHistoryEntry.Alias"/> from (system name, source
    /// id) at record time; unset or null keeps the alias empty.
    /// <see cref="Changed"/> is raised on any entry mutation
    /// (add/evict), never on dedup no-ops. No UI, no persistence, no
    /// disposal: receive threads write, the UI thread reads snapshots.
    /// </summary>
    public sealed class CallHistoryStore
    {
        private const int MinCallHistory = 5;

        private const int MaxCallHistory = 100;

        private readonly object gate = new();

        private readonly int maxCallHistory;

        private readonly Func<string, uint, bool>? suppress;

        private Func<string, uint, string>? aliasResolver;

        private readonly List<CallHistoryEntry> entries = new();

        private readonly Dictionary<string, uint> lastStreamByKey = new();

        /// <summary>
        /// Creates an empty call-history store.
        /// </summary>
        /// <param name="maxCallHistory">The entry cap, clamped to [5,100].</param>
        /// <param name="suppress">Optional suppression filter; returning true for (system name, source id) skips recording.</param>
        public CallHistoryStore(
            int maxCallHistory = 100,
            Func<string, uint, bool>? suppress = null)
        {
            this.maxCallHistory = Math.Clamp(maxCallHistory, MinCallHistory, MaxCallHistory);
            this.suppress = suppress;
        }

        /// <summary>
        /// Sets (or clears) the alias resolver consulted at record
        /// time: each recorded entry's <see cref="CallHistoryEntry.Alias"/>
        /// is resolved from (system name, source id) when a resolver is
        /// set; a null resolver keeps the alias empty (the default).
        /// Thread-safe: the resolver is read inside the store's lock in
        /// <see cref="AddFrame"/>.
        /// </summary>
        /// <param name="resolver">The resolver, or null to clear it.</param>
        public void SetAliasResolver(Func<string, uint, string>? resolver)
        {
            lock (gate)
            {
                aliasResolver = resolver;
            }
        }

        /// <summary>
        /// A snapshot of the recorded entries, newest first. Every access
        /// returns a fresh copy; the live list is never exposed.
        /// </summary>
        public IReadOnlyList<CallHistoryEntry> Entries
        {
            get
            {
                lock (gate)
                {
                    return new List<CallHistoryEntry>(entries);
                }
            }
        }

        /// <summary>
        /// Raised whenever an entry is added or evicted. Never raised for
        /// dedup no-ops or terminator stream clears.
        /// </summary>
        public event Action? Changed;

        /// <summary>
        /// Records one classified receive frame, or applies its stream
        /// bookkeeping. A terminator clears the key's stream and is not
        /// recorded itself; a voice frame of an already-recorded stream
        /// (same key, same stream id) is a dedup no-op; a suppressed
        /// frame (system name, source id) is not recorded. New calls are
        /// inserted at index 0 and the oldest entry is evicted at the
        /// cap.
        /// </summary>
        /// <param name="m">The classified frame metadata.</param>
        /// <param name="channelName">The resolved codeplug channel name, or null for the raw-key fallback.</param>
        public void AddFrame(ReceivedCallMetadata m, string? channelName)
        {
            if (m is null)
            {
                throw new ArgumentNullException(nameof(m));
            }

            Action? changed = null;
            Func<string, uint, string>? resolver;

            lock (gate)
            {
                if (m.IsTerminator)
                {
                    // Close the key's stream before suppression is
                    // consulted. A suppressed terminator still ends the
                    // stream, and the terminator itself is never recorded.
                    lastStreamByKey.Remove(m.Key);
                    return;
                }

                if (suppress is not null && suppress(m.SystemName, m.SrcId))
                {
                    return;
                }

                if (lastStreamByKey.TryGetValue(m.Key, out var lastStream)
                    && lastStream == m.StreamId)
                {
                    // Same call stream: dedup no-op, no mutation, no event.
                    return;
                }

                resolver = aliasResolver;
            }

            // Alias resolution is an injected boundary. Do not hold the
            // store lock while it runs: a slow or re-entrant resolver must
            // not block snapshot readers. A resolver failure degrades to
            // the same empty alias as an unmatched RID.
            var alias = string.Empty;
            if (resolver is not null)
            {
                try
                {
                    alias = resolver(m.SystemName, m.SrcId) ?? string.Empty;
                }
                catch
                {
                    alias = string.Empty;
                }
            }

            lock (gate)
            {
                if (lastStreamByKey.TryGetValue(m.Key, out var lastStream)
                    && lastStream == m.StreamId)
                {
                    // Another receive thread recorded this stream while
                    // the alias was being resolved.
                    return;
                }

                lastStreamByKey[m.Key] = m.StreamId;

                if (entries.Count == maxCallHistory)
                {
                    entries.RemoveAt(entries.Count - 1);
                }

                entries.Insert(0, new CallHistoryEntry(
                    m.Key,
                    channelName ?? string.Empty,
                    m.SystemName,
                    m.SrcId,
                    m.DstId,
                    alias,
                    m.Mode,
                    DateTimeOffset.UtcNow));

                changed = Changed;
            }

            // Raise outside the lock so a subscriber can safely read
            // Entries (or re-enter the store) from the handler.
            changed?.Invoke();
        }
    }
}
