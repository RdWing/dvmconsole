// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using DvmConsole.Avalonia.Services;
using DvmConsole.Platform.Audio;
using dvmconsole;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// Gate 1.4 RED contracts for composition of classified receive metadata,
    /// decoded PCM observation, TX target recording, release-tail capture, and
    /// retention maintenance. These tests deliberately exercise the existing
    /// Core recorder instead of introducing a second recording engine.
    /// </summary>
    public sealed class TarRecordingLifecycleGateTests
    {
        [Fact]
        public void ReceiveMetadataAndDecodedPcm_StartOnceAppendInOrderAndTerminateOnce()
        {
            using var temp = new TempDirectory();
            DateTime start = new DateTime(2026, 8, 10, 15, 0, 0, DateTimeKind.Utc);
            var coordinator = new TarRecordingCoordinator(CreateRecorder(temp.Root), () => start);
            var call = new ReceivedCallMetadata(
                "SYS", 456, 123, 1, VoiceMode.Dmr, 42, "sys|123|slot:1", false);

            coordinator.HandleReceiveFrame(call, "Dispatch", "Radio 456", false, null, null, start);
            coordinator.HandleReceiveFrame(call, "Dispatch", "Radio 456", false, null, null, start.AddMilliseconds(1));
            Assert.IsAssignableFrom<IDecodedPcmObserver>(coordinator);

            coordinator.ObserveDecodedPcm(call.Key, call.Mode, BuildPcm(1600));
            coordinator.ObserveDecodedPcm(call.Key, call.Mode, BuildPcm(1600));
            coordinator.HandleReceiveFrame(
                call with { IsTerminator = true },
                "Dispatch", "Radio 456", false, null, null, start.AddSeconds(1));
            coordinator.HandleReceiveFrame(
                call with { IsTerminator = true },
                "Dispatch", "Radio 456", false, null, null, start.AddSeconds(2));

            string[] sidecars = Directory.GetFiles(temp.Root, "*.json", SearchOption.AllDirectories);
            Assert.Single(sidecars);
            var recordings = coordinator.LoadRecordings();
            var recording = Assert.Single(recordings);
            Assert.Equal(6400L, recording.FileSizeBytes - 44L);
        }

        [Fact]
        public void IdleEnd_IsIdempotentAndDoesNotEndAReusedStream()
        {
            using var temp = new TempDirectory();
            DateTime start = new DateTime(2026, 8, 10, 15, 30, 0, DateTimeKind.Utc);
            var coordinator = new TarRecordingCoordinator(CreateRecorder(temp.Root), () => start);
            var first = new ReceivedCallMetadata(
                "SYS", 456, 123, 1, VoiceMode.Dmr, 42, "sys|123|slot:1", false);
            var second = first with { StreamId = 43 };

            coordinator.HandleReceiveFrame(first, "Dispatch", "", false, null, null, start);
            coordinator.ObserveDecodedPcm(first.Key, first.Mode, BuildPcm(1600));
            coordinator.EndReceive(first.Key, first.Mode, start.AddSeconds(1));
            coordinator.EndReceive(first.Key, first.Mode, start.AddSeconds(2));

            coordinator.HandleReceiveFrame(second, "Dispatch", "", false, null, null, start.AddSeconds(3));
            coordinator.ObserveDecodedPcm(second.Key, second.Mode, BuildPcm(1600));
            coordinator.EndReceive(second.Key, second.Mode, start.AddSeconds(4));

            Assert.Equal(2, Directory.GetFiles(temp.Root, "*.json", SearchOption.AllDirectories).Length);
        }

        [Fact]
        public void TransmitTargetObserver_CapturesPcmThroughReleaseAndStopsOnce()
        {
            using var temp = new TempDirectory();
            DateTime start = new DateTime(2026, 8, 10, 16, 0, 0, DateTimeKind.Utc);
            var coordinator = new TarRecordingCoordinator(CreateRecorder(temp.Root), () => start);
            var target = new TransmitTarget("SYS", "123", 1, VoiceMode.Dmr, 9001);

            Assert.True(coordinator.TryStartTransmit(
                target, "Dispatch", start, out _));
            Assert.IsAssignableFrom<ITransmittedPcmObserver>(coordinator);
            coordinator.ObserveTransmittedPcm(target, BuildPcm(1600));
            coordinator.ObserveTransmittedPcm(target, BuildPcm(1600));
            coordinator.StopAllTransmit(start.AddSeconds(1));
            long fileSizeAfterRelease = new FileInfo(
                Assert.Single(Directory.GetFiles(temp.Root, "*.wav", SearchOption.AllDirectories))).Length;
            coordinator.ObserveTransmittedPcm(target, BuildPcm(1600));
            coordinator.StopAllTransmit(start.AddSeconds(2));

            Assert.Single(Directory.GetFiles(temp.Root, "*.json", SearchOption.AllDirectories));
            Assert.Equal(
                fileSizeAfterRelease,
                new FileInfo(Assert.Single(Directory.GetFiles(temp.Root, "*.wav", SearchOption.AllDirectories))).Length);
        }

        [Fact]
        public void RetentionMaintenance_DeletesExpiredRecordingThroughCoordinator()
        {
            using var temp = new TempDirectory();
            DateTime now = new DateTime(2026, 8, 10, 17, 0, 0, DateTimeKind.Utc);
            var coordinator = new TarRecordingCoordinator(CreateRecorder(temp.Root, enabled: true, retentionDays: 1, now: now));
            var call = new ReceivedCallMetadata(
                "SYS", 456, 123, 1, VoiceMode.Dmr, 77, "sys|123|slot:1", false);
            DateTime start = now.AddDays(-3);

            coordinator.HandleReceiveFrame(call, "Dispatch", "", false, null, null, start);
            coordinator.ObserveDecodedPcm(call.Key, call.Mode, BuildPcm(1600));
            coordinator.EndReceive(call.Key, call.Mode, start.AddSeconds(1));
            Assert.Single(Directory.GetFiles(temp.Root, "*.json", SearchOption.AllDirectories));

            coordinator.RunRetentionMaintenance();
            Assert.Empty(coordinator.LoadRecordings(rebuildIndex: true));
            Assert.Empty(Directory.GetFiles(temp.Root, "*.wav", SearchOption.AllDirectories));
        }

        [Fact]
        public void DisabledConfig_RejectsBeforeSessionAllocation()
        {
            using var temp = new TempDirectory();
            var coordinator = new TarRecordingCoordinator(CreateRecorder(temp.Root, enabled: false, retentionDays: 7));
            var call = new ReceivedCallMetadata(
                "SYS", 456, 123, 1, VoiceMode.Dmr, 78, "sys|123|slot:1", false);

            coordinator.HandleReceiveFrame(call, "Dispatch", "", false, null, null, DateTime.UtcNow);
            coordinator.ObserveDecodedPcm(call.Key, call.Mode, BuildPcm(1600));

            Assert.Empty(Directory.GetFiles(temp.Root, "*.json", SearchOption.AllDirectories));
        }

        private static TarRecorder CreateRecorder(
            string root,
            bool enabled = true,
            int retentionDays = 7,
            DateTime? now = null)
            => new TarRecorder(
                root,
                Path.Combine(root, "default"),
                (_, _, _) => new TarChannelConfig { Enabled = enabled, RetentionDays = retentionDays },
                now is { } fixedNow ? () => fixedNow : null);

        private static byte[] BuildPcm(int sampleCount)
        {
            var pcm = new byte[sampleCount * 2];
            for (var i = 0; i < sampleCount; i++)
            {
                pcm[i * 2] = 0x40;
                pcm[i * 2 + 1] = 0x06;
            }

            return pcm;
        }

        private sealed class TempDirectory : IDisposable
        {
            public TempDirectory()
            {
                Root = Path.Combine(Path.GetTempPath(), "dvmconsole-tar-lifecycle-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Root);
            }

            public string Root { get; }

            public void Dispose()
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
        }
    }
}
