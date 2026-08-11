// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using DvmConsole.Avalonia.ViewModels;

namespace DvmConsole.Avalonia.Services
{
    /// <summary>
    /// Thread-safe receive-state tracker keyed by normalized wire identity.
    /// Receive callbacks update the tracker off-thread; slot mutations are
    /// always routed through the injected UI-post delegate. The tracker is
    /// deliberately independent of audio, protocol decoding and Avalonia.
    /// </summary>
    public sealed class ReceiveProjection : IDisposable
    {
        private static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(2);

        private readonly Action<Action> uiPost;
        private readonly Func<IReadOnlyCollection<ChannelSlotViewModel>> slots;
        private readonly TimeSpan idleTimeout;
        private readonly object sync = new();
        private readonly Dictionary<string, ActiveReceive> active =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> warnings =
            new(StringComparer.OrdinalIgnoreCase);
        private bool disposed;

        /// <summary>
        /// Creates a projection with a two-second WPF-parity idle timeout.
        /// </summary>
        /// <param name="uiPost">Posts slot mutations onto the UI thread.</param>
        /// <param name="slots">Returns the current zone's slot instances.</param>
        /// <param name="idleTimeout">Optional deterministic timeout override for tests.</param>
        public ReceiveProjection(
            Action<Action> uiPost,
            Func<IReadOnlyCollection<ChannelSlotViewModel>> slots,
            TimeSpan? idleTimeout = null)
        {
            this.uiPost = uiPost ?? throw new ArgumentNullException(nameof(uiPost));
            this.slots = slots ?? throw new ArgumentNullException(nameof(slots));
            this.idleTimeout = idleTimeout ?? DefaultIdleTimeout;
            if (this.idleTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(idleTimeout));
            }
        }

        /// <summary>
        /// Records a classified receive frame. Voice starts/refreshes an
        /// active identity; terminators remove it. No slot is touched until
        /// the injected UI-post action runs.
        /// </summary>
        public void Observe(
            ReceivedCallMetadata metadata,
            string? alias,
            DateTimeOffset observedAt)
        {
            if (metadata is null)
            {
                throw new ArgumentNullException(nameof(metadata));
            }

            lock (sync)
            {
                if (disposed)
                {
                    return;
                }

                if (metadata.IsTerminator)
                {
                    active.Remove(metadata.Key);
                }
                else
                {
                    active[metadata.Key] = new ActiveReceive(
                        metadata.Key,
                        observedAt,
                        FormatSource(metadata.SrcId, alias));
                }
            }

            PostApply();
        }

        /// <summary>
        /// Clears identities that have had no classified frame for the
        /// configured timeout. The source display is deliberately retained.
        /// </summary>
        public void SweepIdle(DateTimeOffset now)
        {
            var changed = false;
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }

                var expired = new List<string>();
                foreach (var pair in active)
                {
                    if (now - pair.Value.LastActivity >= idleTimeout)
                    {
                        expired.Add(pair.Key);
                    }
                }

                foreach (var key in expired)
                {
                    changed |= active.Remove(key);
                }
            }

            if (changed)
            {
                PostApply();
            }
        }

        /// <summary>
        /// Clears one normalized identity when the audio router reports an
        /// idle release without a classified terminator.
        /// </summary>
        public void Clear(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            lock (sync)
            {
                if (disposed || !active.Remove(key))
                {
                    return;
                }
            }

            PostApply();
        }

        /// <summary>
        /// Re-applies active receive and warning state to the current slot
        /// instances after a zone collection rebuild.
        /// </summary>
        public void Reproject()
        {
            PostApply();
        }

        /// <summary>
        /// Applies a case-insensitive FNE connection warning to every
        /// current slot belonging to the system. Reconnect clears it.
        /// </summary>
        public void SetFneConnectionWarning(
            string systemName,
            bool connected,
            string? detail)
        {
            if (string.IsNullOrWhiteSpace(systemName))
            {
                return;
            }

            lock (sync)
            {
                if (disposed)
                {
                    return;
                }

                if (connected)
                {
                    warnings.Remove(systemName);
                }
                else
                {
                    warnings[systemName] = string.IsNullOrWhiteSpace(detail)
                        ? "FNE disconnected"
                        : detail;
                }
            }

            PostApply();
        }

        /// <summary>
        /// Makes all subsequent observations and queued UI actions no-ops.
        /// </summary>
        public void Dispose()
        {
            lock (sync)
            {
                disposed = true;
                active.Clear();
                warnings.Clear();
            }
        }

        private void PostApply()
        {
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }
            }

            uiPost(ApplyCurrentState);
        }

        private void ApplyCurrentState()
        {
            List<ActiveReceive> activeSnapshot;
            Dictionary<string, string> warningSnapshot;
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }

                activeSnapshot = new List<ActiveReceive>(active.Values);
                warningSnapshot = new Dictionary<string, string>(
                    warnings,
                    StringComparer.OrdinalIgnoreCase);
            }

            foreach (var slot in slots())
            {
                var state = FindActive(slot, activeSnapshot);
                if (state is null)
                {
                    slot.IsReceiving = false;
                    slot.IsReceivingEncrypted = false;
                }
                else
                {
                    slot.IsReceiving = true;
                    // WPF's DMR receive path does not set encrypted RX from
                    // the PI header; keep this clear until the encryption
                    // metadata gate supplies a source flag.
                    slot.IsReceivingEncrypted = false;
                    slot.LastSrcId = state.LastSource;
                }

                var warning = FindWarning(slot.SystemName, warningSnapshot);
                slot.FneConnectionWarningVisible = warning is not null;
                slot.FneConnectionWarningToolTip = warning ?? string.Empty;
            }
        }

        private static ActiveReceive? FindActive(
            ChannelSlotViewModel slot,
            IReadOnlyList<ActiveReceive> states)
        {
            ActiveReceive? newest = null;
            foreach (var state in states)
            {
                if (!Matches(slot.ResourceKey, state.Key)
                    || (newest is not null && newest.LastActivity >= state.LastActivity))
                {
                    continue;
                }

                newest = state;
            }

            return newest;
        }

        private static string? FindWarning(
            string systemName,
            IReadOnlyDictionary<string, string> warningSnapshot)
        {
            foreach (var pair in warningSnapshot)
            {
                if (string.Equals(pair.Key, systemName, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value;
                }
            }

            return null;
        }

        private static bool Matches(string? resourceKey, string frameKey)
        {
            if (string.IsNullOrWhiteSpace(resourceKey))
            {
                return false;
            }

            return string.Equals(resourceKey, frameKey, StringComparison.OrdinalIgnoreCase)
                || frameKey.StartsWith(resourceKey + "|slot:", StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatSource(uint sourceId, string? alias)
            => string.IsNullOrWhiteSpace(alias)
                ? $"Last ID: {sourceId}"
                : $"Last: {alias.Trim()}";

        private sealed record ActiveReceive(
            string Key,
            DateTimeOffset LastActivity,
            string LastSource);
    }
}
