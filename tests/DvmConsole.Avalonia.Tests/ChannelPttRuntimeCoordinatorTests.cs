// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DvmConsole.Avalonia.Services;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Platform.Audio;
using dvmconsole;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for channel-card momentary PTT. A card press resolves
    /// exactly that slot, owns one shared router capture, clears RX buffers
    /// once, and keeps the slot engaged until asynchronous teardown completes.
    /// </summary>
    public sealed class ChannelPttRuntimeCoordinatorTests
    {
        private sealed class Harness
        {
            public readonly List<IReadOnlyList<TransmitTarget>> Begun = new();
            public readonly List<string> Ended = new();
            public readonly List<IReadOnlyList<TransmitTarget>> Cleared = new();
            public readonly List<string> Statuses = new();
            public TaskCompletionSource<bool>? EndGate;
            public bool CanStart = true;
            public bool TargetAvailable = true;

            public ChannelPttRuntimeCoordinator Create()
                => new(
                    new TransmitTargetResolver(CreateCodeplug()),
                    targets =>
                    {
                        Begun.Add(targets);
                        return Task.CompletedTask;
                    },
                    async () =>
                    {
                        Ended.Add("end");
                        if (EndGate is { } gate)
                        {
                            await gate.Task.ConfigureAwait(false);
                        }
                    },
                    targets => Cleared.Add(targets),
                    () => CanStart,
                    _ => TargetAvailable,
                    status => Statuses.Add(status));
        }

        [Fact]
        public async Task Press_ResolvesOnlyPressedCard_ClearsRxAndEngagesIt()
        {
            var harness = new Harness();
            await using var coordinator = harness.Create();
            var first = MakeSlot("Channel 1", "100");
            var second = MakeSlot("Channel 2", "200");

            await coordinator.HandlePointerDownAsync(first);

            var begun = Assert.Single(harness.Begun);
            Assert.Equal("100", Assert.Single(begun).TalkgroupId);
            Assert.Single(harness.Cleared);
            Assert.True(first.PttEngaged);
            Assert.False(second.PttEngaged);
            Assert.True(coordinator.IsTransmitActive);
            Assert.Equal("100", coordinator.ActiveTarget!.Value.TalkgroupId);
        }

        [Fact]
        public async Task DuplicatePress_IsNoOp_AndReleaseWaitsForAsyncTeardown()
        {
            var harness = new Harness { EndGate = new TaskCompletionSource<bool>() };
            await using var coordinator = harness.Create();
            var slot = MakeSlot("Channel 1", "100");

            await coordinator.HandlePointerDownAsync(slot);
            await coordinator.HandlePointerDownAsync(slot);
            Assert.Single(harness.Begun);

            var release = coordinator.HandlePointerUpAsync();
            await Task.Delay(25);

            Assert.False(release.IsCompleted);
            Assert.True(coordinator.IsTransmitActive);
            Assert.True(slot.PttEngaged);
            Assert.Single(harness.Ended);

            harness.EndGate!.SetResult(true);
            await release;

            Assert.False(coordinator.IsTransmitActive);
            Assert.Null(coordinator.ActiveTarget);
            Assert.False(slot.PttEngaged);
        }

        [Fact]
        public async Task InvalidOrRxOnlyCard_DoesNotStartCapture()
        {
            var harness = new Harness();
            await using var coordinator = harness.Create();
            var unassigned = new ChannelSlotViewModel(1, "CHANNEL 01");
            var rxOnly = MakeSlot("Receive Only", "300", isRxOnly: true);

            await coordinator.HandlePointerDownAsync(unassigned);
            await coordinator.HandlePointerDownAsync(rxOnly);

            Assert.Empty(harness.Begun);
            Assert.Empty(harness.Cleared);
            Assert.False(coordinator.IsTransmitActive);
            Assert.False(unassigned.PttEngaged);
            Assert.False(rxOnly.PttEngaged);
            Assert.Equal(2, harness.Statuses.Count);
        }

        [Fact]
        public async Task CollisionOrUnavailableTarget_DoesNotDisturbExistingState()
        {
            var harness = new Harness { CanStart = false, TargetAvailable = true };
            await using var coordinator = harness.Create();
            var slot = MakeSlot("Channel 1", "100");

            await coordinator.HandlePointerDownAsync(slot);

            Assert.Empty(harness.Begun);
            Assert.Empty(harness.Cleared);
            Assert.False(coordinator.IsTransmitActive);
            Assert.False(slot.PttEngaged);

            harness.CanStart = true;
            harness.TargetAvailable = false;
            await coordinator.HandlePointerDownAsync(slot);

            Assert.Empty(harness.Begun);
            Assert.False(coordinator.IsTransmitActive);
            Assert.False(slot.PttEngaged);
            Assert.Equal(2, harness.Statuses.Count);
        }

        [Fact]
        public async Task BeginFailure_RollsBackCardEngagementAndActiveTarget()
        {
            var codeplug = CreateCodeplug();
            var slot = MakeSlot("Channel 1", "100");
            var beginCalls = 0;
            await using var coordinator = new ChannelPttRuntimeCoordinator(
                new TransmitTargetResolver(codeplug),
                _ =>
                {
                    beginCalls++;
                    throw new InvalidOperationException("capture failed");
                },
                () => Task.CompletedTask,
                _ => { },
                () => true,
                _ => true,
                _ => { });

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => coordinator.HandlePointerDownAsync(slot));

            Assert.Equal(1, beginCalls);
            Assert.False(coordinator.IsTransmitActive);
            Assert.Null(coordinator.ActiveTarget);
            Assert.False(slot.PttEngaged);
        }

        [Fact]
        public async Task ReleaseWithoutPress_IsIdempotent()
        {
            var harness = new Harness();
            await using var coordinator = harness.Create();
            await coordinator.HandlePointerUpAsync();
            await coordinator.HandlePointerUpAsync();

            Assert.Empty(harness.Ended);
            Assert.False(coordinator.IsTransmitActive);
        }

        private static ChannelSlotViewModel MakeSlot(
            string name,
            string talkgroup,
            bool isRxOnly = false)
        {
            var slot = new ChannelSlotViewModel(1, "CHANNEL 01");
            slot.Reassign(
                name,
                talkgroup,
                ResourceIdentity.Build("SYS-1", talkgroup),
                "DMR",
                "SYS-1",
                isRxOnly);
            return slot;
        }

        private static Codeplug CreateCodeplug()
            => new()
            {
                Systems = new List<Codeplug.System>
                {
                    new() { Name = "SYS-1", Rid = "1001" },
                },
                Zones = new List<Codeplug.Zone>
                {
                    new()
                    {
                        Name = "Zone",
                        Channels = new List<Codeplug.Channel>
                        {
                            new() { Name = "Channel 1", System = "SYS-1", Tgid = "100", Slot = 1, Mode = "DMR" },
                            new() { Name = "Channel 2", System = "SYS-1", Tgid = "200", Slot = 1, Mode = "DMR" },
                            new() { Name = "Receive Only", System = "SYS-1", Tgid = "300", Slot = 1, Mode = "DMR", RxOnly = true },
                        },
                    },
                },
            };
    }
}
