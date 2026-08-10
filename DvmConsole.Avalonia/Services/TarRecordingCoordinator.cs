// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Globalization;
using System.Threading;
using dvmconsole;

namespace DvmConsole.Avalonia.Services
{
    /// <summary>
    /// Headless TAR recording lifecycle coordinator: a thin, disposable
    /// lifecycle adapter over the Core <see cref="TarRecorder"/> engine.
    /// Receive frames (<see cref="ReceivedCallMetadata"/>) are translated
    /// into RX <see cref="TarRecordingMetadata"/> (direction, inbound-radio
    /// source type, protocol, system/src/dst/stream identity, channel and
    /// talkgroup names, subscriber alias, encryption fields) and handed to
    /// the recorder with the frame key as the resource key; transmit
    /// metadata is delegated as-is with the supplied lookup values. No
    /// persistence, UI, or event wiring lives here — decoded-PCM routing
    /// (FneReceiveGlue) and PTT shell wiring are later seams. Disposal
    /// stops all active sessions through the injected clock and turns every
    /// later call into a safe no-op; the disposed flag is thread-safe.
    /// </summary>
    public sealed class TarRecordingCoordinator : IDisposable
    {
        private readonly TarRecorder recorder;
        private readonly Func<DateTime> utcNow;
        private int disposed;

        /// <summary>
        /// Creates the coordinator over the Core recorder.
        /// </summary>
        /// <param name="recorder">The Core TAR recording engine; required.</param>
        /// <param name="utcNow">Optional clock provider; defaults to <see cref="DateTime.UtcNow"/>.</param>
        public TarRecordingCoordinator(TarRecorder recorder, Func<DateTime>? utcNow = null)
        {
            this.recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>
        /// Opens an RX recording session for a classified receive frame.
        /// Terminators and null metadata are rejected; the frame key is the
        /// recorder's resource key and the destination id is the legacy
        /// talkgroup id. A zero source id is stored as no subscriber.
        /// Encryption fields follow the WPF oracle's
        /// <c>TarManager.UpdateEncryptionMetadata</c> normalization: an
        /// encrypted session stores the algorithm trimmed (whitespace-only
        /// becomes "Unknown") and keeps the key id only when greater than
        /// zero; an unencrypted session stores an empty algorithm and no
        /// key id.
        /// </summary>
        /// <returns>true when the recorder opened a session; false when
        /// rejected, gated, disposed, or a duplicate.</returns>
        public bool TryStartReceive(
            ReceivedCallMetadata metadata,
            string? channelName,
            string? subscriberAlias,
            bool isEncrypted,
            string? encryptionAlgorithm,
            ushort? encryptionKeyId,
            DateTime startTimeUtc,
            out string sessionKey)
        {
            sessionKey = string.Empty;
            if (Volatile.Read(ref disposed) != 0)
                return false;

            if (metadata == null || metadata.IsTerminator)
                return false;

            var recordingMetadata = new TarRecordingMetadata
            {
                Direction = TarRecordingDirection.RX,
                RecordingSourceType = TarRecordingSourceType.InboundRadio,
                Protocol = metadata.Mode.ToString().ToUpperInvariant(),
                UtcStartTime = startTimeUtc,
                SystemName = metadata.SystemName ?? string.Empty,
                ChannelName = channelName ?? string.Empty,
                TalkgroupId = metadata.DstId,
                TalkgroupName = channelName ?? string.Empty,
                SubscriberId = metadata.SrcId == 0 ? (uint?)null : metadata.SrcId,
                SubscriberAlias = subscriberAlias?.Trim() ?? string.Empty,
                IsEncrypted = isEncrypted,
                EncryptionAlgorithm = isEncrypted
                    ? (string.IsNullOrWhiteSpace(encryptionAlgorithm) ? "Unknown" : encryptionAlgorithm.Trim())
                    : string.Empty,
                EncryptionKeyId = isEncrypted && encryptionKeyId.GetValueOrDefault() > 0
                    ? encryptionKeyId
                    : null,
                StreamId = metadata.StreamId,
            };

            return recorder.TryStartRecording(
                recordingMetadata,
                metadata.Key ?? string.Empty,
                channelName,
                metadata.DstId.ToString(CultureInfo.InvariantCulture),
                out sessionKey);
        }

        /// <summary>
        /// Opens a TX recording session, delegating the supplied metadata and
        /// lookup values to the recorder unchanged. Null or non-TX metadata is
        /// rejected.
        /// </summary>
        /// <returns>true when the recorder opened a session; false when
        /// rejected, gated, disposed, or a duplicate.</returns>
        public bool TryStartTransmit(
            TarRecordingMetadata metadata,
            string resourceKey,
            string? legacyChannelName,
            string? legacyTalkgroupId,
            out string sessionKey)
        {
            sessionKey = string.Empty;
            if (Volatile.Read(ref disposed) != 0)
                return false;

            if (metadata == null || metadata.Direction != TarRecordingDirection.TX)
                return false;

            return recorder.TryStartRecording(
                metadata,
                resourceKey,
                legacyChannelName,
                legacyTalkgroupId,
                out sessionKey);
        }

        /// <summary>
        /// Appends raw 16-bit little-endian PCM to the active session. No-op
        /// after disposal (or for unknown sessions, as in the recorder).
        /// </summary>
        public void AppendAudio(string sessionKey, byte[] pcm)
        {
            if (Volatile.Read(ref disposed) != 0)
                return;

            recorder.AppendAudio(sessionKey, pcm);
        }

        /// <summary>
        /// Ends the session and writes the finished recording. Always false
        /// after disposal.
        /// </summary>
        /// <returns>true when a recording was written; false for unknown
        /// keys, empty buffers, no audible content, or disposal.</returns>
        public bool TryStopRecording(string sessionKey, DateTime endTimeUtc, out TarRecordingMetadata? recorded)
        {
            recorded = null;
            if (Volatile.Read(ref disposed) != 0)
                return false;

            return recorder.TryStopRecording(sessionKey, endTimeUtc, out recorded);
        }

        /// <summary>
        /// Stops every active session with a single end time. No-op after
        /// disposal.
        /// </summary>
        public void StopAllSessions(DateTime endTimeUtc)
        {
            if (Volatile.Read(ref disposed) != 0)
                return;

            recorder.StopAllSessions(endTimeUtc);
        }

        /// <summary>
        /// Marks the coordinator disposed exactly once and stops all active
        /// sessions at the injected clock's current time. Idempotent and
        /// thread-safe: concurrent or repeated calls are no-ops, and every
        /// subsequent start/append/stop is a safe no-op.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            recorder.StopAllSessions(utcNow());
        }
    }
}
