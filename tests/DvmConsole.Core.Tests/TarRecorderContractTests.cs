// SPDX-License-Identifier: AGPL-3.0-only
/**
* RED contract gate for the headless TAR recorder engine.
*/
using System.Buffers.Binary;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using dvmconsole;

namespace DvmConsole.Core.Tests
{
    public sealed class TarRecorderContractTests
    {
        [Fact]
        public void StartAppendStop_WritesTrimmedWaveAndPascalCaseSidecar()
        {
            using TempDir temp = new TempDir();
            DateTime start = new DateTime(2026, 8, 9, 12, 34, 56, 789, DateTimeKind.Utc);
            DateTime end = start.AddMilliseconds(500);
            TarRecorder recorder = new TarRecorder(
                configuredRootPath: temp.Root,
                defaultRootPath: Path.Combine(temp.Root, "default"),
                configResolver: (_, _, _) => new TarChannelConfig
                {
                    Enabled = true,
                    RetentionDays = 7
                });
            TarRecordingMetadata request = new TarRecordingMetadata
            {
                RecordingId = "12345678",
                Direction = TarRecordingDirection.RX,
                RecordingSourceType = TarRecordingSourceType.InboundRadio,
                Protocol = "DMR",
                UtcStartTime = start,
                SystemName = "System",
                ChannelName = "Dispatch",
                TalkgroupId = 123,
                TalkgroupName = "Dispatch",
                SubscriberId = 456,
                SubscriberAlias = "Radio 456",
                StreamId = 42
            };

            Assert.True(recorder.TryStartRecording(request, "system|123", "Dispatch", "123", out string sessionKey));
            recorder.AppendAudio(sessionKey, BuildPcm(1600, 1600, 1600, 1000));

            Assert.True(recorder.TryStopRecording(sessionKey, end, out TarRecordingMetadata recorded));
            Assert.NotNull(recorded);
            Assert.Equal(new DateTime(2026, 8, 9), recorded.UtcStartTime.Date);
            Assert.Equal(440L, recorded.DurationMs);
            Assert.Equal(80, recorded.TrimLeadMs);
            Assert.Equal(80, recorded.TrimTailMs);
            string hourFolder = Path.GetDirectoryName(recorded.FilePath);
            string talkgroupFolder = Path.GetDirectoryName(hourFolder);
            Assert.Equal("12", Path.GetFileName(hourFolder));
            Assert.Equal("Dispatch_TG123", Path.GetFileName(talkgroupFolder));
            Assert.Equal("12345678", recorded.RecordingId);
            Assert.True(File.Exists(recorded.FilePath));
            Assert.Equal(44 + (440 * 16), new FileInfo(recorded.FilePath).Length);

            byte[] wav = File.ReadAllBytes(recorded.FilePath);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
            Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
            Assert.Equal((short)1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22, 2)));
            Assert.Equal(8000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24, 4)));
            Assert.Equal((short)16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34, 2)));
            Assert.Equal(wav.Length - 44, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40, 4)));

            string sidecarPath = Path.ChangeExtension(recorded.FilePath, ".json");
            Assert.True(File.Exists(sidecarPath));
            JObject sidecar = JObject.Parse(File.ReadAllText(sidecarPath));
            Assert.Equal("RX", (string)sidecar["Direction"]);
            Assert.Equal("InboundRadio", (string)sidecar["RecordingSourceType"]);
            Assert.Equal(440L, (long)sidecar["DurationMs"]);
            Assert.Equal(80, (int)sidecar["TrimLeadMs"]);
            Assert.Equal(80, (int)sidecar["TrimTailMs"]);
            Assert.Null(sidecar["sessionKey"]);
        }

        [Fact]
        public void StopAllSessions_FinalizesEveryActiveSession()
        {
            using TempDir temp = new TempDir();
            DateTime start = new DateTime(2026, 8, 9, 13, 0, 0, DateTimeKind.Utc);
            TarRecorder recorder = CreateRecorder(temp.Root);
            TarRecordingMetadata rx = CreateMetadata(TarRecordingDirection.RX, 101, 11, start);
            TarRecordingMetadata tx = CreateMetadata(TarRecordingDirection.TX, 202, 12, start.AddSeconds(1));

            Assert.True(recorder.TryStartRecording(rx, "system|101", "Dispatch", "101", out string rxKey));
            Assert.True(recorder.TryStartRecording(tx, "system|101", "Dispatch", "101", out string txKey));
            recorder.AppendAudio(rxKey, BuildPcm(0, 1600, 0, 1000));
            recorder.AppendAudio(txKey, BuildPcm(0, 1600, 0, 1000));

            recorder.StopAllSessions(start.AddSeconds(2));

            string[] sidecars = Directory.GetFiles(temp.Root, "*.json", SearchOption.AllDirectories);
            Assert.Equal(2, sidecars.Length);
            Assert.Contains(sidecars, path => Path.GetFileName(path).Contains("_RX_", StringComparison.Ordinal));
            Assert.Contains(sidecars, path => Path.GetFileName(path).Contains("_TX_", StringComparison.Ordinal));
        }

        [Fact]
        public void StartRecording_AppliesConfigGatesAndSeparatesRxTxKeys()
        {
            using TempDir temp = new TempDir();
            TarChannelConfig config = new TarChannelConfig();
            TarRecorder recorder = new TarRecorder(
                configuredRootPath: temp.Root,
                defaultRootPath: Path.Combine(temp.Root, "default"),
                configResolver: (_, _, _) => config);
            DateTime start = new DateTime(2026, 8, 9, 14, 0, 0, DateTimeKind.Utc);

            TarRecordingMetadata zeroStream = CreateMetadata(TarRecordingDirection.RX, 101, 0, start);
            Assert.False(recorder.TryStartRecording(zeroStream, "system|101", "Dispatch", "101", out _));

            config.Enabled = true;
            config.IgnoredSubscriberIds.Add(101);
            TarRecordingMetadata ignored = CreateMetadata(TarRecordingDirection.RX, 101, 42, start);
            Assert.False(recorder.TryStartRecording(ignored, "system|101", "Dispatch", "101", out _));

            config.IgnoredSubscriberIds.Clear();
            TarRecordingMetadata rx = CreateMetadata(TarRecordingDirection.RX, 101, 42, start);
            TarRecordingMetadata tx = CreateMetadata(TarRecordingDirection.TX, 101, 42, start);
            Assert.True(recorder.TryStartRecording(rx, "system|101", "Dispatch", "101", out string rxKey));
            Assert.True(recorder.TryStartRecording(tx, "system|101", "Dispatch", "101", out string txKey));
            Assert.Equal("RX|System|101|42", rxKey);
            Assert.Equal("TX|System|101|42", txKey);
        }

        [Fact]
        public void TryEnsureRecordingRoot_TrimsAndCreatesValidPath()
        {
            using TempDir temp = new TempDir();
            string requested = Path.Combine(temp.Root, "nested", "recordings");

            Assert.True(TarRecorder.TryEnsureRecordingRoot(
                "  " + requested + "  ",
                out string normalized,
                out string error));
            Assert.Equal(requested, normalized);
            Assert.True(Directory.Exists(requested));
            Assert.Equal(string.Empty, error);

            Assert.False(TarRecorder.TryEnsureRecordingRoot("  ", out _, out error));
            Assert.NotEqual(string.Empty, error);
        }

        [Fact]
        public void LoadRecordings_RebuildsIndexAndSortsNewestFirst()
        {
            using TempDir temp = new TempDir();
            TarRecorder recorder = CreateRecorder(temp.Root);
            DateTime olderStart = new DateTime(2026, 8, 9, 15, 0, 0, DateTimeKind.Utc);
            DateTime newerStart = olderStart.AddMinutes(1);

            WriteRecording(recorder, CreateMetadata(TarRecordingDirection.RX, 201, 21, olderStart, "older001"), olderStart.AddSeconds(1));
            WriteRecording(recorder, CreateMetadata(TarRecordingDirection.RX, 202, 22, newerStart, "newer001"), newerStart.AddSeconds(1));

            IReadOnlyList<TarRecordingMetadata> recordings = recorder.LoadRecordings(rebuildIndex: true);

            Assert.Equal(2, recordings.Count);
            Assert.Equal("newer001", recordings[0].RecordingId);
            Assert.Equal("older001", recordings[1].RecordingId);
            Assert.True(File.Exists(Path.Combine(temp.Root, "tar-recording-index.cache")));
        }

        [Fact]
        public void LoadRecordings_UsesCacheUntilExplicitRebuild()
        {
            using TempDir temp = new TempDir();
            TarRecorder recorder = CreateRecorder(temp.Root);
            DateTime originalStart = new DateTime(2026, 8, 9, 16, 0, 0, DateTimeKind.Utc);
            TarRecordingMetadata metadata = CreateMetadata(TarRecordingDirection.RX, 301, 31, originalStart, "cache001");
            WriteRecording(recorder, metadata, originalStart.AddSeconds(1));

            Assert.Single(recorder.LoadRecordings(rebuildIndex: true));
            string sidecarPath = Path.ChangeExtension(metadata.FilePath, ".json");
            metadata.UtcStartTime = originalStart.AddHours(1);
            File.WriteAllText(sidecarPath, JsonConvert.SerializeObject(metadata, Formatting.Indented));

            IReadOnlyList<TarRecordingMetadata> cached = recorder.LoadRecordings();
            IReadOnlyList<TarRecordingMetadata> rebuilt = recorder.LoadRecordings(rebuildIndex: true);
            Assert.Equal(originalStart, cached[0].UtcStartTime);
            Assert.Equal(originalStart.AddHours(1), rebuilt[0].UtcStartTime);
        }

        [Fact]
        public void DeleteRecording_RemovesFilesAndRetentionDeletesExpiredRecordings()
        {
            using TempDir temp = new TempDir();
            DateTime now = new DateTime(2026, 8, 9, 17, 0, 0, DateTimeKind.Utc);
            TarChannelConfig config = new TarChannelConfig { Enabled = true, RetentionDays = 1 };
            TarRecorder recorder = new TarRecorder(
                configuredRootPath: temp.Root,
                defaultRootPath: Path.Combine(temp.Root, "default"),
                configResolver: (_, _, _) => config,
                utcNow: () => now);
            TarRecordingMetadata old = CreateMetadata(TarRecordingDirection.RX, 401, 41, now.AddDays(-2), "old00001");
            WriteRecording(recorder, old, now.AddDays(-2).AddHours(1));
            string oldWavPath = old.FilePath;
            string oldJsonPath = Path.ChangeExtension(oldWavPath, ".json");

            recorder.RunRetentionMaintenance();

            Assert.False(File.Exists(oldWavPath));
            Assert.False(File.Exists(oldJsonPath));

            TarRecordingMetadata current = CreateMetadata(TarRecordingDirection.RX, 402, 42, now, "keep0001");
            WriteRecording(recorder, current, now.AddSeconds(1));
            string currentWavPath = current.FilePath;
            string currentJsonPath = Path.ChangeExtension(currentWavPath, ".json");
            recorder.DeleteRecording(current);
            Assert.False(File.Exists(currentWavPath));
            Assert.False(File.Exists(currentJsonPath));
        }

        [Fact]
        public void LoadRecordings_WhenNestedDirectoryCannotBeEnumerated_ReturnsEmpty()
        {
            if (OperatingSystem.IsWindows())
                return;

            using TempDir temp = new TempDir();
            string blockedPath = Path.Combine(temp.Root, "blocked");
            Directory.CreateDirectory(blockedPath);
            File.SetUnixFileMode(blockedPath, UnixFileMode.None);

            try
            {
                TarRecorder recorder = CreateRecorder(temp.Root);

                IReadOnlyList<TarRecordingMetadata> recordings = recorder.LoadRecordings(rebuildIndex: true);

                Assert.Empty(recordings);
            }
            finally
            {
                File.SetUnixFileMode(
                    blockedPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        private static TarRecorder CreateRecorder(string root)
        {
            return new TarRecorder(
                configuredRootPath: root,
                defaultRootPath: Path.Combine(root, "default"),
                configResolver: (_, _, _) => new TarChannelConfig
                {
                    Enabled = true,
                    RetentionDays = 7
                });
        }

        private static TarRecordingMetadata CreateMetadata(TarRecordingDirection direction, uint subscriberId, uint streamId, DateTime start, string recordingId = null)
        {
            return new TarRecordingMetadata
            {
                RecordingId = recordingId ?? (direction == TarRecordingDirection.RX ? "rx000001" : "tx000001"),
                Direction = direction,
                RecordingSourceType = direction == TarRecordingDirection.RX
                    ? TarRecordingSourceType.InboundRadio
                    : TarRecordingSourceType.ConsoleTx,
                Protocol = "DMR",
                UtcStartTime = start,
                SystemName = "System",
                ChannelName = "Dispatch",
                TalkgroupId = 101,
                TalkgroupName = "Dispatch",
                SubscriberId = subscriberId,
                StreamId = streamId
            };
        }

        private static void WriteRecording(TarRecorder recorder, TarRecordingMetadata metadata, DateTime end)
        {
            Assert.True(recorder.TryStartRecording(metadata, "system|101", "Dispatch", "101", out string key));
            recorder.AppendAudio(key, BuildPcm(0, 1600, 0, 1000));
            Assert.True(recorder.TryStopRecording(key, end, out _));
        }

        private static byte[] BuildPcm(int leadingSilenceSamples, int activeSamples, int trailingSilenceSamples, short amplitude)
        {
            short[] samples = new short[leadingSilenceSamples + activeSamples + trailingSilenceSamples];
            for (int index = leadingSilenceSamples; index < leadingSilenceSamples + activeSamples; index++)
                samples[index] = amplitude;

            byte[] pcm = new byte[samples.Length * 2];
            Buffer.BlockCopy(samples, 0, pcm, 0, pcm.Length);
            return pcm;
        }

        private sealed class TempDir : IDisposable
        {
            public TempDir()
            {
                Root = Path.Combine(Path.GetTempPath(), "dvmconsole-tar-recorder-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Root);
            }

            public string Root { get; }

            public void Dispose()
            {
                try
                {
                    if (Directory.Exists(Root))
                        Directory.Delete(Root, recursive: true);
                }
                catch
                {
                }
            }
        }
    }
}
