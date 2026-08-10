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

using System.Globalization;
using System.IO;
using System.Text;

using Newtonsoft.Json;

namespace dvmconsole
{
    /// <summary>
    /// Headless TAR recording engine: session lifecycle, silence-trimmed 8 kHz /
    /// 16-bit / mono WAVE persistence, PascalCase JSON sidecar, a per-root
    /// recording index cache, retention maintenance, and recording deletion.
    /// Behavior ported from the WPF TarManager oracle with injected config
    /// resolution, recording root selection, and clock (no SettingsManager,
    /// no static Log, no NAudio).
    /// </summary>
    public sealed class TarRecorder
    {
        private const int SampleRate = 8000;
        private const int BitsPerSample = 16;
        private const int ChannelCount = 1;
        private const short SilenceThreshold = 400;
        private const int WindowSamples = 160; // 20 ms at 8 kHz
        private const int TrimPaddingMs = 120;
        private const string RecordingIndexFileName = "tar-recording-index.cache";

        private readonly string configuredRootPath;
        private readonly string defaultRootPath;
        private readonly Func<string, string, string, TarChannelConfig> configResolver;
        private readonly Func<DateTime> utcNow;
        private readonly object syncRoot = new object();
        private readonly object indexSyncRoot = new object();
        private readonly Dictionary<string, TarActiveSession> activeSessions =
            new Dictionary<string, TarActiveSession>(StringComparer.OrdinalIgnoreCase);

        private sealed class TarActiveSession
        {
            public TarRecordingMetadata Metadata { get; init; }
            public MemoryStream PcmBuffer { get; } = new MemoryStream();
            public object SyncRoot { get; } = new object();
        }

        private sealed class TarTrimResult
        {
            public byte[] AudioBytes { get; init; } = Array.Empty<byte>();
            public int TrimLeadMs { get; init; }
            public int TrimTailMs { get; init; }
        }

        private sealed class TarRecordingIndex
        {
            public int SchemaVersion { get; set; } = 1;
            public List<TarRecordingIndexEntry> Entries { get; set; } = new List<TarRecordingIndexEntry>();
        }

        private sealed class TarRecordingIndexEntry
        {
            public string MetadataPath { get; set; } = string.Empty;
            public DateTime MetadataLastWriteUtc { get; set; }
            public string RecordingPath { get; set; } = string.Empty;
            public DateTime RecordingLastWriteUtc { get; set; }
            public TarRecordingMetadata Metadata { get; set; }
        }

        /// <summary>
        /// Constructs the recorder with an injected config resolver and recording
        /// root pair. <paramref name="configuredRootPath"/> wins when non-whitespace
        /// (trimmed); otherwise <paramref name="defaultRootPath"/> is used — the same
        /// normalization ternary as the WPF app via <see cref="TarRecordingsPath.Resolve"/>.
        /// </summary>
        /// <param name="configuredRootPath">Configured TAR recordings root; whitespace falls back.</param>
        /// <param name="defaultRootPath">Fallback recordings root.</param>
        /// <param name="configResolver">Resolves the persisted channel config for a resource.</param>
        /// <param name="utcNow">Optional clock provider; defaults to <see cref="DateTime.UtcNow"/>.</param>
        public TarRecorder(
            string configuredRootPath,
            string defaultRootPath,
            Func<string, string, string, TarChannelConfig> configResolver,
            Func<DateTime> utcNow = null)
        {
            this.configuredRootPath = configuredRootPath;
            this.defaultRootPath = defaultRootPath;
            this.configResolver = configResolver ?? throw new ArgumentNullException(nameof(configResolver));
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>
        /// Resolves the active recordings root, normalizing like the WPF app:
        /// whitespace-only configured root falls back to the default root.
        /// </summary>
        public string ResolveRecordingRoot()
            => TarRecordingsPath.Resolve(configuredRootPath, defaultRootPath);

        /// <summary>
        /// Normalizes a recordings root (trimming whitespace) and creates it.
        /// </summary>
        /// <param name="rootPath">Requested root; surrounding whitespace is trimmed.</param>
        /// <param name="normalizedPath">The normalized path, or an empty string when the
        /// requested root is whitespace-only.</param>
        /// <param name="errorMessage">Empty on success; non-empty on whitespace-only
        /// input or creation failure.</param>
        /// <returns>true when the trimmed root was created; false otherwise.</returns>
        public static bool TryEnsureRecordingRoot(string rootPath, out string normalizedPath, out string errorMessage)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                normalizedPath = string.Empty;
                errorMessage = "Recording root path is empty or whitespace-only.";
                return false;
            }

            normalizedPath = rootPath.Trim();
            errorMessage = string.Empty;
            try
            {
                Directory.CreateDirectory(normalizedPath);
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Opens a new recording session when the resolved channel config is enabled
        /// and the stream/subscriber pass the WPF gates.
        /// </summary>
        /// <param name="metadata">Session metadata; carries the direction, system,
        /// talkgroup and stream identity used to build the session key.</param>
        /// <param name="resourceKey">Resource key passed to the config resolver.</param>
        /// <param name="legacyName">Legacy channel name passed to the config resolver.</param>
        /// <param name="legacyTalkgroupId">Legacy talkgroup id passed to the config resolver.</param>
        /// <param name="sessionKey">Distinct per-direction session key
        /// (e.g. <c>RX|System|123|42</c>) when the session starts.</param>
        /// <returns>true when a session was opened; false when gated or a duplicate.</returns>
        public bool TryStartRecording(
            TarRecordingMetadata metadata,
            string resourceKey,
            string legacyName,
            string legacyTalkgroupId,
            out string sessionKey)
        {
            sessionKey = string.Empty;
            if (metadata == null || metadata.StreamId.GetValueOrDefault() == 0)
                return false;

            TarChannelConfig config = configResolver(resourceKey, legacyName, legacyTalkgroupId);
            if (config == null || !config.Enabled)
                return false;

            if (metadata.Direction != TarRecordingDirection.TX &&
                metadata.SubscriberId.HasValue &&
                config.IgnoredSubscriberIds != null &&
                config.IgnoredSubscriberIds.Contains(metadata.SubscriberId.Value))
            {
                return false;
            }

            if (!TryEnsureRecordingRoot(ResolveRecordingRoot(), out _, out _))
                return false;

            sessionKey = BuildSessionKey(metadata);
            lock (syncRoot)
            {
                if (activeSessions.ContainsKey(sessionKey))
                {
                    sessionKey = string.Empty;
                    return false;
                }

                activeSessions[sessionKey] = new TarActiveSession
                {
                    Metadata = metadata
                };
            }

            return true;
        }

        /// <summary>
        /// Appends raw 16-bit little-endian PCM to the active session. No-op for
        /// unknown session keys or empty buffers.
        /// </summary>
        public void AppendAudio(string sessionKey, byte[] pcm)
        {
            if (string.IsNullOrWhiteSpace(sessionKey) || pcm == null || pcm.Length == 0)
                return;

            TarActiveSession session;
            lock (syncRoot)
            {
                if (!activeSessions.TryGetValue(sessionKey, out session))
                    return;
            }

            lock (session.SyncRoot)
                session.PcmBuffer.Write(pcm, 0, pcm.Length);
        }

        /// <summary>
        /// Ends the session, trimming leading/trailing silence, then synchronously
        /// writes the trimmed WAVE and a PascalCase JSON sidecar. On success
        /// <paramref name="recorded"/> is the finished metadata (same instance passed
        /// in at start, completed in place like the WPF oracle).
        /// </summary>
        /// <returns>true when a recording was written; false for unknown keys,
        /// empty buffers, or no audible content.</returns>
        public bool TryStopRecording(string sessionKey, DateTime endTimeUtc, out TarRecordingMetadata recorded)
        {
            recorded = null;
            if (string.IsNullOrWhiteSpace(sessionKey))
                return false;

            TarActiveSession session;
            lock (syncRoot)
            {
                if (!activeSessions.TryGetValue(sessionKey, out session))
                    return false;
                activeSessions.Remove(sessionKey);
            }

            recorded = FinalizeSession(session, endTimeUtc);
            return recorded != null;
        }

        /// <summary>
        /// Synchronously finalizes every active session with a single end time,
        /// mirroring the WPF app's shutdown path (all sessions stopped together).
        /// </summary>
        public void StopAllSessions(DateTime endTimeUtc)
        {
            List<TarActiveSession> sessions;
            lock (syncRoot)
            {
                sessions = activeSessions.Values.ToList();
                activeSessions.Clear();
            }

            foreach (TarActiveSession session in sessions)
                FinalizeSession(session, endTimeUtc);
        }

        /// <summary>
        /// Loads persisted recordings. Without an explicit rebuild, an existing
        /// recording index cache is authoritative (fast path); with
        /// <paramref name="rebuildIndex"/> set, sidecars are re-scanned and the
        /// cache rewritten. Recordings sort newest-first.
        /// </summary>
        public IReadOnlyList<TarRecordingMetadata> LoadRecordings(bool rebuildIndex = false)
        {
            List<TarRecordingMetadata> recordings = new List<TarRecordingMetadata>();
            string rootPath = ResolveRecordingRoot();
            if (!Directory.Exists(rootPath))
                return recordings;

            lock (indexSyncRoot)
            {
                Dictionary<string, TarRecordingIndexEntry> cachedEntries = LoadRecordingIndex(rootPath);
                if (!rebuildIndex && cachedEntries.Count > 0)
                    return SortRecordings(cachedEntries.Values.Select(entry => entry.Metadata));

                return RebuildRecordingIndex(rootPath);
            }
        }

        /// <summary>
        /// Deletes the completed recording's WAVE and sidecar, and drops its cache
        /// entry. Matches the WPF DeleteRecording behavior.
        /// </summary>
        public void DeleteRecording(TarRecordingMetadata metadata)
        {
            if (metadata == null)
                return;

            string sidecarPath = GetSidecarPath(metadata.FilePath);
            DeleteRecordingFile(sidecarPath);
            DeleteRecordingFile(metadata.FilePath);

            if (TryEnsureRecordingRoot(ResolveRecordingRoot(), out string rootPath, out _))
                RemoveRecordingIndexEntries(rootPath, new[] { sidecarPath });
        }

        /// <summary>
        /// Removes recordings whose per-channel retention window has elapsed. Uses
        /// the injected clock (<c>utcNow</c>) so tests can freeze time; config is
        /// resolved per recording through the injected resolver.
        /// </summary>
        public void RunRetentionMaintenance()
        {
            if (!TryEnsureRecordingRoot(ResolveRecordingRoot(), out string rootPath, out _))
                return;

            List<string> deletedMetadataPaths = new List<string>();
            foreach (TarRecordingMetadata metadata in LoadRecordings())
            {
                if (metadata == null || string.IsNullOrWhiteSpace(metadata.ChannelName))
                    continue;

                string talkgroupId = metadata.TalkgroupId?.ToString(CultureInfo.InvariantCulture);
                string configKey = ResourceIdentity.Build(metadata.SystemName, talkgroupId);
                TarChannelConfig config = configResolver(configKey, metadata.ChannelName, talkgroupId);
                if (config == null || config.RetentionDays <= 0)
                    continue;

                if (metadata.UtcEndTime >= utcNow().AddDays(-config.RetentionDays))
                    continue;

                string sidecarPath = GetSidecarPath(metadata.FilePath);
                DeleteRecordingFile(sidecarPath);
                DeleteRecordingFile(metadata.FilePath);
                deletedMetadataPaths.Add(sidecarPath);
            }

            if (deletedMetadataPaths.Count > 0)
                RemoveRecordingIndexEntries(rootPath, deletedMetadataPaths);
        }

        private TarRecordingMetadata FinalizeSession(TarActiveSession session, DateTime endTimeUtc)
        {
            if (session?.Metadata == null)
                return null;

            byte[] pcmBytes;
            lock (session.SyncRoot)
                pcmBytes = session.PcmBuffer.ToArray();

            if (pcmBytes.Length == 0)
                return null;

            TarTrimResult trim = TrimSilence(pcmBytes);
            if (trim.AudioBytes.Length == 0)
                return null;

            try
            {
                TarRecordingMetadata metadata = session.Metadata;

                string rootPath = ResolveRecordingRoot();
                string dayFolder = Path.Combine(rootPath, metadata.UtcStartTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                string talkgroupFolder = Path.Combine(dayFolder, BuildTalkgroupFolderName(metadata));
                string hourFolder = Path.Combine(talkgroupFolder, metadata.UtcStartTime.ToString("HH", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(hourFolder);

                string fileBaseName = BuildRecordingBaseFileName(metadata);
                string wavPath = Path.Combine(hourFolder, fileBaseName + ".wav");
                string metadataPath = Path.Combine(hourFolder, fileBaseName + ".json");

                TarWavWriter.Write(wavPath, trim.AudioBytes, SampleRate, (short)BitsPerSample, (short)ChannelCount);

                metadata.UtcEndTime = NormalizeUtc(endTimeUtc);
                metadata.DurationMs = Math.Max(0, (long)Math.Round(trim.AudioBytes.Length / 16.0));
                metadata.FilePath = wavPath;
                metadata.FileName = Path.GetFileName(wavPath);
                metadata.FileSizeBytes = new FileInfo(wavPath).Length;
                metadata.SampleRate = SampleRate;
                metadata.BitsPerSample = BitsPerSample;
                metadata.ChannelCount = ChannelCount;
                metadata.TrimLeadMs = trim.TrimLeadMs;
                metadata.TrimTailMs = trim.TrimTailMs;

                File.WriteAllText(metadataPath, JsonConvert.SerializeObject(metadata, Formatting.Indented), Encoding.UTF8);

                UpdateRecordingIndexEntry(rootPath, metadataPath, metadata);

                return metadata;
            }
            catch
            {
                return null;
            }
        }

        private IReadOnlyList<TarRecordingMetadata> RebuildRecordingIndex(string rootPath)
        {
            List<TarRecordingMetadata> recordings = new List<TarRecordingMetadata>();
            Dictionary<string, TarRecordingIndexEntry> cachedEntries = LoadRecordingIndex(rootPath);
            List<TarRecordingIndexEntry> updatedIndexEntries = new List<TarRecordingIndexEntry>();
            bool indexChanged = false;

            List<string> metadataFiles;
            try
            {
                metadataFiles = Directory.GetFiles(rootPath, "*.json", SearchOption.AllDirectories).ToList();
            }
            catch
            {
                // A failed scan (e.g. an unreadable nested directory) must not
                // propagate; yield no recordings, matching the WPF oracle.
                return recordings;
            }

            foreach (string metadataFile in metadataFiles)
            {
                try
                {
                    // An explicit rebuild must reflect on-disk changes, so the
                    // sidecar is always re-read (no mtime-based cache fast path).
                    FileInfo metadataInfo = new FileInfo(metadataFile);
                    TarRecordingMetadata metadata = JsonConvert.DeserializeObject<TarRecordingMetadata>(File.ReadAllText(metadataFile));
                    indexChanged = true;

                    if (metadata == null || string.IsNullOrWhiteSpace(metadata.FilePath) || !File.Exists(metadata.FilePath))
                    {
                        indexChanged = true;
                        continue;
                    }

                    recordings.Add(metadata);
                    updatedIndexEntries.Add(new TarRecordingIndexEntry
                    {
                        MetadataPath = metadataFile,
                        MetadataLastWriteUtc = metadataInfo.LastWriteTimeUtc,
                        RecordingPath = metadata.FilePath,
                        RecordingLastWriteUtc = new FileInfo(metadata.FilePath).LastWriteTimeUtc,
                        Metadata = metadata
                    });
                }
                catch
                {
                    indexChanged = true;
                }
            }

            if (indexChanged || updatedIndexEntries.Count != cachedEntries.Count)
                SaveRecordingIndex(rootPath, updatedIndexEntries);

            return SortRecordings(recordings);
        }

        private static IReadOnlyList<TarRecordingMetadata> SortRecordings(IEnumerable<TarRecordingMetadata> recordings)
            => recordings
                .OrderByDescending(recording => recording.UtcStartTime)
                .ThenBy(recording => recording.FileName, StringComparer.Ordinal)
                .ToList();

        private static string GetSidecarPath(string wavPath)
            => string.IsNullOrWhiteSpace(wavPath)
                ? string.Empty
                : Path.ChangeExtension(wavPath, ".json");

        private static void DeleteRecordingFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;

            try
            {
                File.Delete(path);
            }
            catch
            {
                // Tolerant deletion, matching the WPF oracle.
            }
        }

        private void UpdateRecordingIndexEntry(string rootPath, string metadataPath, TarRecordingMetadata metadata)
        {
            lock (indexSyncRoot)
            {
                Dictionary<string, TarRecordingIndexEntry> entries = LoadRecordingIndex(rootPath);
                entries[metadataPath] = new TarRecordingIndexEntry
                {
                    MetadataPath = metadataPath,
                    MetadataLastWriteUtc = new FileInfo(metadataPath).LastWriteTimeUtc,
                    RecordingPath = metadata.FilePath,
                    RecordingLastWriteUtc = new FileInfo(metadata.FilePath).LastWriteTimeUtc,
                    Metadata = metadata
                };
                SaveRecordingIndex(rootPath, entries.Values);
            }
        }

        private void RemoveRecordingIndexEntries(string rootPath, IEnumerable<string> metadataPaths)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || metadataPaths == null)
                return;

            List<string> paths = metadataPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (paths.Count == 0)
                return;

            lock (indexSyncRoot)
            {
                Dictionary<string, TarRecordingIndexEntry> entries = LoadRecordingIndex(rootPath);
                bool removed = false;
                foreach (string metadataPath in paths)
                    removed |= entries.Remove(metadataPath);

                if (removed)
                    SaveRecordingIndex(rootPath, entries.Values);
            }
        }

        private static Dictionary<string, TarRecordingIndexEntry> LoadRecordingIndex(string rootPath)
        {
            string indexPath = Path.Combine(rootPath, RecordingIndexFileName);
            if (!File.Exists(indexPath))
                return new Dictionary<string, TarRecordingIndexEntry>(StringComparer.OrdinalIgnoreCase);

            try
            {
                TarRecordingIndex index = JsonConvert.DeserializeObject<TarRecordingIndex>(File.ReadAllText(indexPath));
                List<TarRecordingIndexEntry> entries = index?.Entries ?? new List<TarRecordingIndexEntry>();
                return entries
                    .Where(entry => !string.IsNullOrWhiteSpace(entry?.MetadataPath) && entry.Metadata != null)
                    .GroupBy(entry => entry.MetadataPath, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, TarRecordingIndexEntry>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static void SaveRecordingIndex(string rootPath, IEnumerable<TarRecordingIndexEntry> entries)
        {
            try
            {
                TarRecordingIndex index = new TarRecordingIndex
                {
                    Entries = entries.ToList()
                };
                File.WriteAllText(
                    Path.Combine(rootPath, RecordingIndexFileName),
                    JsonConvert.SerializeObject(index, Formatting.Indented),
                    Encoding.UTF8);
            }
            catch
            {
                // Tolerant cache-save: disk / permission / root-deletion
                // failures must not propagate through LoadRecordings,
                // DeleteRecording, or RunRetentionMaintenance.
            }
        }

        private static string BuildSessionKey(TarRecordingMetadata metadata)
        {
            string direction = metadata.Direction == TarRecordingDirection.TX ? "TX" : "RX";
            string system = metadata.SystemName?.Trim() ?? string.Empty;
            string talkgroupId = metadata.TalkgroupId.HasValue
                ? metadata.TalkgroupId.Value.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
            string streamId = metadata.StreamId.HasValue
                ? metadata.StreamId.Value.ToString(CultureInfo.InvariantCulture)
                : "0";
            return $"{direction}|{system}|{talkgroupId}|{streamId}";
        }

        private static string BuildRecordingBaseFileName(TarRecordingMetadata metadata)
        {
            string timestamp = metadata.UtcStartTime.ToString("yyyyMMdd'T'HHmmss.fff'Z'", CultureInfo.InvariantCulture);
            string system = SanitizeSegment(metadata.SystemName, 18);
            string channel = SanitizeSegment(metadata.ChannelName, 18);
            string tg = metadata.TalkgroupId.HasValue ? metadata.TalkgroupId.Value.ToString(CultureInfo.InvariantCulture) : "TG0";
            string src = metadata.SubscriberId.HasValue ? metadata.SubscriberId.Value.ToString(CultureInfo.InvariantCulture) : "SRC0";
            string shortId = string.IsNullOrWhiteSpace(metadata.RecordingId)
                ? Guid.NewGuid().ToString("N")[..8]
                : metadata.RecordingId[..Math.Min(8, metadata.RecordingId.Length)];

            return $"{timestamp}_{metadata.Direction}_{system}_{channel}_TG{tg}_SRC{src}_{shortId}";
        }

        private static string BuildTalkgroupFolderName(TarRecordingMetadata metadata)
        {
            string talkgroupName = SanitizeSegment(metadata?.TalkgroupName, 48);
            string talkgroupId = metadata?.TalkgroupId?.ToString(CultureInfo.InvariantCulture) ?? "0";
            return $"{talkgroupName}_TG{talkgroupId}";
        }

        private static string SanitizeSegment(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "unknown";

            StringBuilder builder = new StringBuilder();
            foreach (char character in value.Trim())
            {
                if (Path.GetInvalidFileNameChars().Contains(character))
                    continue;

                if (char.IsLetterOrDigit(character))
                    builder.Append(character);
                else
                    builder.Append('_');
            }

            string sanitized = builder.ToString().Trim('_');
            if (string.IsNullOrWhiteSpace(sanitized))
                sanitized = "unknown";

            if (sanitized.Length > maxLength)
                sanitized = sanitized[..maxLength];

            return sanitized;
        }

        private static DateTime NormalizeUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc)
                return value;
            if (value.Kind == DateTimeKind.Local)
                return value.ToUniversalTime();

            return DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime();
        }

        private static TarTrimResult TrimSilence(byte[] pcmBytes)
        {
            if (pcmBytes == null || pcmBytes.Length < 2)
            {
                return new TarTrimResult
                {
                    AudioBytes = Array.Empty<byte>(),
                    TrimLeadMs = 0,
                    TrimTailMs = 0
                };
            }

            int totalSamples = pcmBytes.Length / 2;
            int firstActiveSample = FindFirstActiveSample(pcmBytes, totalSamples);
            int lastActiveSample = FindLastActiveSample(pcmBytes, totalSamples);

            if (firstActiveSample < 0 || lastActiveSample < firstActiveSample)
            {
                // All-silence: WPF writes the buffer untouched with zero trim.
                return new TarTrimResult
                {
                    AudioBytes = pcmBytes.ToArray(),
                    TrimLeadMs = 0,
                    TrimTailMs = 0
                };
            }

            int paddingSamples = (SampleRate * TrimPaddingMs) / 1000;
            int startSample = Math.Max(0, firstActiveSample - paddingSamples);
            int endSample = Math.Min(totalSamples - 1, lastActiveSample + paddingSamples);

            int startByte = startSample * 2;
            int byteLength = ((endSample - startSample) + 1) * 2;
            byte[] trimmedBytes = new byte[byteLength];
            Buffer.BlockCopy(pcmBytes, startByte, trimmedBytes, 0, byteLength);

            int trimLeadMs = (int)Math.Round(startSample * 1000.0 / SampleRate);
            int trimTailSamples = Math.Max(0, totalSamples - endSample - 1);
            int trimTailMs = (int)Math.Round(trimTailSamples * 1000.0 / SampleRate);

            return new TarTrimResult
            {
                AudioBytes = trimmedBytes,
                TrimLeadMs = trimLeadMs,
                TrimTailMs = trimTailMs
            };
        }

        private static int FindFirstActiveSample(byte[] pcmBytes, int totalSamples)
        {
            for (int sampleIndex = 0; sampleIndex < totalSamples; sampleIndex += WindowSamples)
            {
                int samplesToCheck = Math.Min(WindowSamples, totalSamples - sampleIndex);
                if (WindowHasActivity(pcmBytes, sampleIndex, samplesToCheck))
                    return sampleIndex;
            }

            return -1;
        }

        private static int FindLastActiveSample(byte[] pcmBytes, int totalSamples)
        {
            for (int sampleIndex = Math.Max(0, totalSamples - WindowSamples); sampleIndex >= 0; sampleIndex -= WindowSamples)
            {
                int samplesToCheck = Math.Min(WindowSamples, totalSamples - sampleIndex);
                if (WindowHasActivity(pcmBytes, sampleIndex, samplesToCheck))
                    return sampleIndex + samplesToCheck - 1;
            }

            return -1;
        }

        private static bool WindowHasActivity(byte[] pcmBytes, int startSample, int sampleCount)
        {
            int byteIndex = startSample * 2;
            for (int i = 0; i < sampleCount; i++)
            {
                short sample = BitConverter.ToInt16(pcmBytes, byteIndex + (i * 2));
                if (Math.Abs(sample) >= SilenceThreshold)
                    return true;
            }

            return false;
        }
    } // public sealed class TarRecorder
}
