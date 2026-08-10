// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.IO;
using dvmconsole;
using DvmConsole.Avalonia.Services;
using DvmConsole.Platform.Audio;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for the headless TAR lifecycle adapter. Receive/TX
    /// transport wiring and PCM production remain later shell seams.
    /// </summary>
    public sealed class TarRecordingCoordinatorTests
    {
        [Fact]
        public void ReceiveLifecycle_StartAppendStop_DelegatesToCoreRecorder()
        {
            using var temp = new TempDirectory();
            DateTime start = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);
            string? resolvedResourceKey = null;
            string? resolvedChannelName = null;
            string? resolvedTalkgroupId = null;
            var recorder = CreateRecorder(temp.Root, (resourceKey, channelName, talkgroupId) =>
            {
                resolvedResourceKey = resourceKey;
                resolvedChannelName = channelName;
                resolvedTalkgroupId = talkgroupId;
            });
            var coordinator = new TarRecordingCoordinator(recorder, () => start);
            var call = new ReceivedCallMetadata(
                "SYS",
                456,
                123,
                1,
                VoiceMode.Dmr,
                42,
                "sys|123",
                false);

            Assert.True(coordinator.TryStartReceive(
                call,
                "Dispatch",
                " Radio 456 ",
                isEncrypted: true,
                encryptionAlgorithm: " AES-256 ",
                encryptionKeyId: 9,
                start,
                out string sessionKey));

            coordinator.AppendAudio(sessionKey, BuildPcm(3200, 1600));

            Assert.True(coordinator.TryStopRecording(
                sessionKey,
                start.AddSeconds(1),
                out TarRecordingMetadata recorded));
            Assert.NotNull(recorded);
            Assert.Equal(TarRecordingDirection.RX, recorded.Direction);
            Assert.Equal(TarRecordingSourceType.InboundRadio, recorded.RecordingSourceType);
            Assert.Equal("DMR", recorded.Protocol);
            Assert.Equal(start, recorded.UtcStartTime);
            Assert.Equal("SYS", recorded.SystemName);
            Assert.Equal("Dispatch", recorded.ChannelName);
            Assert.Equal("Dispatch", recorded.TalkgroupName);
            Assert.Equal((uint)123, recorded.TalkgroupId);
            Assert.Equal((uint)456, recorded.SubscriberId);
            Assert.Equal("Radio 456", recorded.SubscriberAlias);
            Assert.True(recorded.IsEncrypted);
            Assert.Equal("AES-256", recorded.EncryptionAlgorithm);
            Assert.Equal((ushort)9, recorded.EncryptionKeyId);
            Assert.Equal("sys|123", resolvedResourceKey);
            Assert.Equal("Dispatch", resolvedChannelName);
            Assert.Equal("123", resolvedTalkgroupId);
            Assert.True(File.Exists(recorded.FilePath));
        }

        [Fact]
        public void ReceiveEncryptionMetadata_UsesWpfNormalization()
        {
            using var temp = new TempDirectory();
            DateTime start = new DateTime(2026, 8, 10, 12, 30, 0, DateTimeKind.Utc);
            var coordinator = new TarRecordingCoordinator(CreateRecorder(temp.Root));
            var encryptedCall = new ReceivedCallMetadata(
                "SYS", 456, 123, 1, VoiceMode.Dmr, 45, "sys|123", false);

            Assert.True(coordinator.TryStartReceive(
                encryptedCall,
                "Dispatch",
                "",
                isEncrypted: true,
                encryptionAlgorithm: "   ",
                encryptionKeyId: 0,
                startTimeUtc: start,
                out string encryptedSessionKey));
            coordinator.AppendAudio(encryptedSessionKey, BuildPcm(3200, 1600));
            Assert.True(coordinator.TryStopRecording(
                encryptedSessionKey,
                start.AddSeconds(1),
                out TarRecordingMetadata encrypted));
            Assert.Equal("Unknown", encrypted.EncryptionAlgorithm);
            Assert.Null(encrypted.EncryptionKeyId);

            var clearCall = encryptedCall with { StreamId = 46 };
            Assert.True(coordinator.TryStartReceive(
                clearCall,
                "Dispatch",
                "",
                isEncrypted: false,
                encryptionAlgorithm: " AES ",
                encryptionKeyId: 9,
                startTimeUtc: start,
                out string clearSessionKey));
            coordinator.AppendAudio(clearSessionKey, BuildPcm(3200, 1600));
            Assert.True(coordinator.TryStopRecording(
                clearSessionKey,
                start.AddSeconds(1),
                out TarRecordingMetadata clear));
            Assert.False(clear.IsEncrypted);
            Assert.Equal(string.Empty, clear.EncryptionAlgorithm);
            Assert.Null(clear.EncryptionKeyId);
        }

        [Fact]
        public void ReceiveWithZeroSourceId_StoresNoSubscriber()
        {
            using var temp = new TempDirectory();
            DateTime start = new DateTime(2026, 8, 10, 12, 45, 0, DateTimeKind.Utc);
            var coordinator = new TarRecordingCoordinator(CreateRecorder(temp.Root));
            var call = new ReceivedCallMetadata(
                "SYS", 0, 123, 1, VoiceMode.Dmr, 50, "sys|123", false);

            Assert.True(coordinator.TryStartReceive(
                call,
                "Dispatch",
                "",
                isEncrypted: false,
                encryptionAlgorithm: null,
                encryptionKeyId: null,
                startTimeUtc: start,
                out string sessionKey));
            coordinator.AppendAudio(sessionKey, BuildPcm(3200, 1600));
            Assert.True(coordinator.TryStopRecording(
                sessionKey,
                start.AddSeconds(1),
                out TarRecordingMetadata recorded));
            Assert.Null(recorded.SubscriberId);
        }

        [Fact]
        public void StartGates_RejectNullTerminatorAndWrongDirection()
        {
            using var temp = new TempDirectory();
            var coordinator = new TarRecordingCoordinator(CreateRecorder(temp.Root));
            var call = new ReceivedCallMetadata(
                "SYS", 456, 123, 1, VoiceMode.Dmr, 47, "sys|123", false);

            Assert.False(coordinator.TryStartReceive(
                null!, "Dispatch", "", false, null, null, DateTime.UtcNow, out string nullSessionKey));
            Assert.Empty(nullSessionKey);
            Assert.False(coordinator.TryStartReceive(
                call with { IsTerminator = true }, "Dispatch", "", false, null, null,
                DateTime.UtcNow, out string terminatorSessionKey));
            Assert.Empty(terminatorSessionKey);
            Assert.False(coordinator.TryStartTransmit(
                new TarRecordingMetadata
                {
                    Direction = TarRecordingDirection.RX,
                    StreamId = 48,
                    Protocol = "DMR"
                },
                "sys|123", "Dispatch", "123", out string wrongDirectionSessionKey));
            Assert.Empty(wrongDirectionSessionKey);
        }

        [Fact]
        public void TransmitLifecycle_StartAppendStop_UsesTxMetadataAndSession()
        {
            using var temp = new TempDirectory();
            DateTime start = new DateTime(2026, 8, 10, 13, 0, 0, DateTimeKind.Utc);
            string? resolvedResourceKey = null;
            string? resolvedChannelName = null;
            string? resolvedTalkgroupId = null;
            var coordinator = new TarRecordingCoordinator(CreateRecorder(
                temp.Root,
                (resourceKey, channelName, talkgroupId) =>
                {
                    resolvedResourceKey = resourceKey;
                    resolvedChannelName = channelName;
                    resolvedTalkgroupId = talkgroupId;
                }));
            var metadata = new TarRecordingMetadata
            {
                Direction = TarRecordingDirection.TX,
                RecordingSourceType = TarRecordingSourceType.ConsoleTx,
                Protocol = "DMR",
                UtcStartTime = start,
                SystemName = "SYS",
                ChannelName = "Dispatch",
                TalkgroupId = 123,
                TalkgroupName = "Dispatch",
                StreamId = 43,
            };

            Assert.True(coordinator.TryStartTransmit(
                metadata,
                "sys|123",
                "Dispatch",
                "123",
                out string sessionKey));
            Assert.Equal("sys|123", resolvedResourceKey);
            Assert.Equal("Dispatch", resolvedChannelName);
            Assert.Equal("123", resolvedTalkgroupId);
            coordinator.AppendAudio(sessionKey, BuildPcm(3200, 1600));

            Assert.True(coordinator.TryStopRecording(
                sessionKey,
                start.AddSeconds(1),
                out TarRecordingMetadata recorded));
            Assert.Same(metadata, recorded);
            Assert.Equal(TarRecordingDirection.TX, recorded.Direction);
            Assert.True(File.Exists(recorded.FilePath));
        }

        [Fact]
        public void Dispose_StopsActiveSessions()
        {
            using var temp = new TempDirectory();
            DateTime now = new DateTime(2026, 8, 10, 14, 0, 0, DateTimeKind.Utc);
            var coordinator = new TarRecordingCoordinator(CreateRecorder(temp.Root), () => now);
            var call = new ReceivedCallMetadata(
                "SYS",
                456,
                123,
                1,
                VoiceMode.Dmr,
                44,
                "sys|123",
                false);

            Assert.True(coordinator.TryStartReceive(
                call,
                "Dispatch",
                "",
                isEncrypted: false,
                encryptionAlgorithm: string.Empty,
                encryptionKeyId: null,
                now,
                out string sessionKey));
            coordinator.AppendAudio(sessionKey, BuildPcm(3200, 1600));

            coordinator.Dispose();
            coordinator.Dispose();
            coordinator.AppendAudio(sessionKey, BuildPcm(3200, 1600));

            Assert.False(coordinator.TryStartReceive(
                call with { StreamId = 49 }, "Dispatch", "", false, null, null,
                now, out string postDisposeSessionKey));
            Assert.Empty(postDisposeSessionKey);
            Assert.False(coordinator.TryStopRecording(
                sessionKey, now.AddSeconds(1), out TarRecordingMetadata? postDisposeRecording));
            Assert.Null(postDisposeRecording);

            Assert.NotEmpty(Directory.GetFiles(temp.Root, "*.json", SearchOption.AllDirectories));
        }

        private static TarRecorder CreateRecorder(
            string root,
            Action<string, string, string>? captureResolver = null)
            => new TarRecorder(
                root,
                Path.Combine(root, "default"),
                (resourceKey, channelName, talkgroupId) =>
                {
                    captureResolver?.Invoke(resourceKey, channelName, talkgroupId);
                    return new TarChannelConfig { Enabled = true, RetentionDays = 7 };
                });

        private static byte[] BuildPcm(int sampleCount, short amplitude)
        {
            var pcm = new byte[sampleCount * 2];
            for (var i = 0; i < sampleCount; i++)
            {
                pcm[i * 2] = (byte)(amplitude & 0xFF);
                pcm[i * 2 + 1] = (byte)(amplitude >> 8);
            }

            return pcm;
        }

        private sealed class TempDirectory : IDisposable
        {
            public TempDirectory()
            {
                Root = Path.Combine(Path.GetTempPath(), "dvmconsole-tar-coordinator-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Root);
            }

            public string Root { get; }

            public void Dispose()
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
        }
    }
}
