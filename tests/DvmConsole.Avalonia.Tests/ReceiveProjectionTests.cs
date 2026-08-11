// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using DvmConsole.Avalonia.Services;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Platform.Audio;
using dvmconsole;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for Gate 2.3's normalized receive/TX card projection.
    /// The projection tracks wire state off-thread and applies slot state only
    /// through the injected UI-post seam.
    /// </summary>
    public sealed class ReceiveProjectionTests
    {
        [Fact]
        public void DmrVoice_StartsOnlyTheMatchingNormalizedIdentity()
        {
            var queue = new ConcurrentQueue<Action>();
            var first = Slot("System 1", "77", "DMR");
            var other = Slot("System 1", "88", "DMR");
            var otherSystem = Slot("System 2", "77", "DMR");
            var slots = new List<ChannelSlotViewModel> { first, other, otherSystem };
            using var projection = NewProjection(queue, () => slots);

            projection.Observe(
                new ReceivedCallMetadata(
                    "SYSTEM 1", 100, 77, 0, VoiceMode.Dmr, 7,
                    "system 1|77|slot:0", false),
                "Unit 7",
                At(0));

            Assert.False(first.IsReceiving);
            Drain(queue);
            Assert.True(first.IsReceiving);
            Assert.Equal("Last: Unit 7", first.LastSrcId);
            Assert.False(other.IsReceiving);
            Assert.False(otherSystem.IsReceiving);
        }

        [Fact]
        public void P25Voice_IgnoresTheDmrSlotComponent()
        {
            var queue = new ConcurrentQueue<Action>();
            var slot = Slot("System 1", "77", "P25");
            var slots = new List<ChannelSlotViewModel> { slot };
            using var projection = NewProjection(queue, () => slots);

            projection.Observe(
                new ReceivedCallMetadata(
                    "System 1", 100, 77, 9, VoiceMode.P25, 8,
                    "system 1|77", false),
                alias: null,
                At(0));
            Drain(queue);

            Assert.True(slot.IsReceiving);
            Assert.False(slot.IsReceivingEncrypted);
            Assert.Equal("Last ID: 100", slot.LastSrcId);
        }

        [Fact]
        public void Terminator_ClearsReceiveStateButKeepsLastSource()
        {
            var queue = new ConcurrentQueue<Action>();
            var slot = Slot("System 1", "77", "DMR");
            var slots = new List<ChannelSlotViewModel> { slot };
            using var projection = NewProjection(queue, () => slots);
            var key = "system 1|77|slot:0";

            projection.Observe(Voice(key, VoiceMode.Dmr), "Unit 7", At(0));
            Drain(queue);
            projection.Observe(Terminator(key, VoiceMode.Dmr), null, At(1));
            Drain(queue);

            Assert.False(slot.IsReceiving);
            Assert.False(slot.IsReceivingEncrypted);
            Assert.Equal("Last: Unit 7", slot.LastSrcId);
        }

        [Fact]
        public void IdleSweep_AfterTwoSeconds_ClearsReceiveState()
        {
            var queue = new ConcurrentQueue<Action>();
            var slot = Slot("System 1", "77", "DMR");
            var slots = new List<ChannelSlotViewModel> { slot };
            using var projection = NewProjection(queue, () => slots);

            projection.Observe(Voice("system 1|77|slot:0", VoiceMode.Dmr), null, At(0));
            Drain(queue);
            projection.SweepIdle(At(2.1));
            Drain(queue);

            Assert.False(slot.IsReceiving);
            Assert.Equal("Last ID: 100", slot.LastSrcId);
        }

        [Fact]
        public void ZoneSwitch_ReprojectsActiveStateByNormalizedIdentity()
        {
            var queue = new ConcurrentQueue<Action>();
            var oldSlot = Slot("System 1", "77", "DMR");
            var current = new List<ChannelSlotViewModel> { oldSlot };
            using var projection = NewProjection(queue, () => current);

            projection.Observe(Voice("system 1|77|slot:0", VoiceMode.Dmr), "Unit 7", At(0));
            Drain(queue);
            var replacement = Slot("SYSTEM 1", "77", "DMR");
            current = new List<ChannelSlotViewModel> { replacement };

            projection.Reproject();
            Drain(queue);

            Assert.True(replacement.IsReceiving);
            Assert.Equal("Last: Unit 7", replacement.LastSrcId);
        }

        [Fact]
        public void FneWarning_IsCaseInsensitiveAndClearsOnReconnect()
        {
            var queue = new ConcurrentQueue<Action>();
            var slot = Slot("System 1", "77", "DMR");
            var slots = new List<ChannelSlotViewModel> { slot };
            using var projection = NewProjection(queue, () => slots);

            projection.SetFneConnectionWarning("SYSTEM 1", false, "Disconnected");
            Drain(queue);
            Assert.True(slot.FneConnectionWarningVisible);
            Assert.Equal("Disconnected", slot.FneConnectionWarningToolTip);

            projection.SetFneConnectionWarning("system 1", true, null);
            Drain(queue);
            Assert.False(slot.FneConnectionWarningVisible);
            Assert.Equal(string.Empty, slot.FneConnectionWarningToolTip);
        }

        [Fact]
        public void ConcurrentFrames_AreLockGuardedAndUiMarshalled()
        {
            var queue = new ConcurrentQueue<Action>();
            var slot = Slot("System 1", "77", "DMR");
            var slots = new List<ChannelSlotViewModel> { slot };
            using var projection = NewProjection(queue, () => slots);

            Parallel.For(0, 64, i => projection.Observe(
                Voice("system 1|77|slot:0", VoiceMode.Dmr, (uint)i),
                null,
                At(i)));

            Assert.False(slot.IsReceiving);
            Assert.True(queue.Count > 0);
            Drain(queue);
            Assert.True(slot.IsReceiving);
        }

        [Fact]
        public void Dispose_MakesLateFramesAndWarningsNoOps()
        {
            var queue = new ConcurrentQueue<Action>();
            var slot = Slot("System 1", "77", "DMR");
            var slots = new List<ChannelSlotViewModel> { slot };
            var projection = NewProjection(queue, () => slots);
            projection.Dispose();

            projection.Observe(Voice("system 1|77|slot:0", VoiceMode.Dmr), null, At(0));
            projection.SetFneConnectionWarning("System 1", false, "Disconnected");
            projection.Reproject();
            projection.SweepIdle(At(3));

            Assert.Empty(queue);
            Assert.False(slot.IsReceiving);
            Assert.False(slot.FneConnectionWarningVisible);
        }

        private static ReceiveProjection NewProjection(
            ConcurrentQueue<Action> queue,
            Func<IReadOnlyCollection<ChannelSlotViewModel>> slots)
            => new(action => queue.Enqueue(action), slots);

        private static ChannelSlotViewModel Slot(string system, string tgid, string mode)
        {
            var slot = new ChannelSlotViewModel(1, "CHANNEL 01");
            slot.Reassign(
                "Channel " + tgid,
                tgid,
                ResourceIdentity.Build(system, tgid),
                mode,
                system,
                isRxOnly: false,
                cardSize: "normal",
                idleColor: "#234567");
            return slot;
        }

        private static ReceivedCallMetadata Voice(
            string key,
            VoiceMode mode,
            uint streamId = 7)
            => new("System 1", 100, 77, 0, mode, streamId, key, false);

        private static ReceivedCallMetadata Terminator(string key, VoiceMode mode)
            => new("System 1", 100, 77, 0, mode, 7, key, true);

        private static DateTimeOffset At(double seconds)
            => DateTimeOffset.UnixEpoch.AddSeconds(seconds);

        private static void Drain(ConcurrentQueue<Action> queue)
        {
            while (queue.TryDequeue(out var action))
            {
                action();
            }
        }
    }
}
