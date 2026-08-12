// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DvmConsole.Avalonia.Services;
using DvmConsole.Platform.Audio;
using dvmconsole;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for Gate 4.4 patch/multi-select PTT runtime composition.
    ///
    /// Patch-group PTT is a separate transmit request path from the dashboard
    /// PTT and from the Gate 4.3 receive-forward coordinator. It resolves the
    /// copied member snapshot at press time, fans one capture out to the
    /// ordered resolved targets, and keeps its transmit-active window true
    /// until the asynchronous router teardown has completed.
    /// </summary>
    public sealed class PatchPttRuntimeCoordinatorTests
    {
        private sealed class Harness
        {
            public readonly List<IReadOnlyList<TransmitTarget>> Begun = new();
            public readonly List<string> Ended = new();
            public readonly List<IReadOnlyList<TransmitTarget>> ClearedFor = new();
            public readonly List<string> Statuses = new();
            public TaskCompletionSource<bool>? EndGate;
            public Func<bool>? CanStart;
            public Func<TransmitTarget, bool>? ForwardActive;

            public PatchPttRuntimeCoordinator Create()
            {
                return new PatchPttRuntimeCoordinator(
                    new TransmitTargetResolver(MakeCodeplug()),
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
                    targets => ClearedFor.Add(targets),
                    () => CanStart?.Invoke() ?? true,
                    _ => true,
                    status => Statuses.Add(status),
                    target => ForwardActive?.Invoke(target) == true);
            }
        }

        [Fact]
        public void ApiShape_ExposesIndependentAsyncPatchPttLifecycle()
        {
            var type = typeof(PatchPttRuntimeCoordinator);

            Assert.True(type.IsPublic);
            Assert.True(type.IsSealed);
            Assert.NotNull(type.GetConstructor(new[]
            {
                typeof(TransmitTargetResolver),
                typeof(Func<IReadOnlyList<TransmitTarget>, Task>),
                typeof(Func<Task>),
                typeof(Action<IReadOnlyList<TransmitTarget>>),
                typeof(Func<bool>)
            }));
            Assert.NotNull(type.GetMethod(nameof(PatchPttRuntimeCoordinator.HandleRequestAsync)));
            Assert.NotNull(type.GetProperty(nameof(PatchPttRuntimeCoordinator.IsTransmitActive)));
            Assert.NotNull(type.GetProperty(nameof(PatchPttRuntimeCoordinator.ActiveTargets)));
        }

        [Fact]
        public async Task Start_ResolvesOrderedMemberSnapshot_SkipsInvalidAndClearsRxOnce()
        {
            var harness = new Harness();
            await using var coordinator = harness.Create();

            await coordinator.HandleRequestAsync(
                "Patch A",
                isActive: true,
                new[]
                {
                    new PatchTalkgroupMember { SystemName = "SYS-2", Tgid = "200" },
                    new PatchTalkgroupMember { SystemName = "missing", Tgid = "999" },
                    new PatchTalkgroupMember { SystemName = "SYS-1", Tgid = "100" },
                });

            var begun = Assert.Single(harness.Begun);
            Assert.Equal(new[] { "200", "100" }, begun.Select(target => target.TalkgroupId));
            Assert.Single(harness.ClearedFor);
            Assert.True(coordinator.IsTransmitActive);
            Assert.Equal(new[] { "200", "100" }, coordinator.ActiveTargets.Select(target => target.TalkgroupId));
        }

        [Fact]
        public async Task Release_KeepsTransmitActiveUntilAsyncTeardownCompletes()
        {
            var harness = new Harness { EndGate = new TaskCompletionSource<bool>() };
            await using var coordinator = harness.Create();

            await coordinator.HandleRequestAsync(
                "Patch A",
                isActive: true,
                new[] { new PatchTalkgroupMember { SystemName = "SYS-1", Tgid = "100" } });

            var release = coordinator.HandleRequestAsync("Patch A", false, Array.Empty<PatchTalkgroupMember>());
            await Task.Delay(25);

            Assert.False(release.IsCompleted);
            Assert.True(coordinator.IsTransmitActive);
            Assert.Single(harness.Ended);

            harness.EndGate!.SetResult(true);
            await release;

            Assert.False(coordinator.IsTransmitActive);
            Assert.Empty(coordinator.ActiveTargets);
        }

        [Fact]
        public async Task MultipleGroups_UseIndependentGroupStateAndReconcileUnion()
        {
            var harness = new Harness();
            await using var coordinator = harness.Create();

            await coordinator.HandleRequestAsync(
                "Patch A",
                isActive: true,
                new[] { new PatchTalkgroupMember { SystemName = "SYS-1", Tgid = "100" } });
            await coordinator.HandleRequestAsync(
                "Select A",
                isActive: true,
                new[] { new PatchTalkgroupMember { SystemName = "SYS-2", Tgid = "200" } });

            Assert.Equal(2, harness.Begun.Count);
            Assert.Equal(new[] { "100", "200" }, harness.Begun[1].Select(target => target.TalkgroupId));
            Assert.Single(harness.Ended);
            Assert.Equal(new[] { "100", "200" }, coordinator.ActiveTargets.Select(target => target.TalkgroupId));

            await coordinator.HandleRequestAsync("Patch A", false, Array.Empty<PatchTalkgroupMember>());

            Assert.Equal(3, harness.Begun.Count);
            Assert.Equal(new[] { "200" }, harness.Begun[2].Select(target => target.TalkgroupId));
            Assert.Equal(2, harness.Ended.Count);
            Assert.True(coordinator.IsTransmitActive);
        }

        [Fact]
        public async Task EmptyOrUnknownMembers_DoNotStartTransmit()
        {
            var harness = new Harness();
            await using var coordinator = harness.Create();

            await coordinator.HandleRequestAsync(
                "Patch A",
                isActive: true,
                new[] { new PatchTalkgroupMember { SystemName = "missing", Tgid = "999" } });

            Assert.Empty(harness.Begun);
            Assert.Empty(harness.ClearedFor);
            Assert.False(coordinator.IsTransmitActive);
            Assert.Empty(coordinator.ActiveTargets);
            Assert.Contains(harness.Statuses, status => status.Contains("no valid transmit targets", StringComparison.Ordinal));
        }

        [Fact]
        public async Task CollisionWithDashboardPtt_RejectsStartWithoutDisturbingRunningGroup()
        {
            var harness = new Harness();
            await using var coordinator = harness.Create();

            await coordinator.HandleRequestAsync(
                "Patch A",
                isActive: true,
                new[] { new PatchTalkgroupMember { SystemName = "SYS-1", Tgid = "100" } });
            Assert.Single(harness.Begun);

            harness.CanStart = () => false;
            await coordinator.HandleRequestAsync(
                "Select A",
                isActive: true,
                new[] { new PatchTalkgroupMember { SystemName = "SYS-2", Tgid = "200" } });

            // The rejected request must not tear down the running group's
            // shared capture nor clear its state (deterministic rejection).
            Assert.Single(harness.Begun);
            Assert.Empty(harness.Ended);
            Assert.True(coordinator.IsTransmitActive);
            Assert.Equal(new[] { "100" }, coordinator.ActiveTargets.Select(target => target.TalkgroupId));
            Assert.Contains(harness.Statuses, status => status.Contains("blocked", StringComparison.Ordinal));

            // The running group still releases normally afterwards.
            harness.CanStart = null;
            await coordinator.HandleRequestAsync("Patch A", false, Array.Empty<PatchTalkgroupMember>());
            Assert.Single(harness.Ended);
            Assert.False(coordinator.IsTransmitActive);
        }

        [Fact]
        public async Task DoubleRelease_EndsTransmitTailExactlyOnce()
        {
            var harness = new Harness();
            await using var coordinator = harness.Create();

            await coordinator.HandleRequestAsync(
                "Patch A",
                isActive: true,
                new[] { new PatchTalkgroupMember { SystemName = "SYS-1", Tgid = "100" } });

            await coordinator.HandleRequestAsync("Patch A", false, Array.Empty<PatchTalkgroupMember>());
            await coordinator.HandleRequestAsync("Patch A", false, Array.Empty<PatchTalkgroupMember>());

            Assert.Single(harness.Ended);
            Assert.False(coordinator.IsTransmitActive);
            Assert.Empty(coordinator.ActiveTargets);
        }

        [Fact]
        public async Task ForwardActiveMember_IsSkippedWithoutStartingCapture()
        {
            var harness = new Harness
            {
                ForwardActive = target => target.TalkgroupId == "100"
            };
            await using var coordinator = harness.Create();

            await coordinator.HandleRequestAsync(
                "Patch A",
                isActive: true,
                new[]
                {
                    new PatchTalkgroupMember { SystemName = "SYS-1", Tgid = "100" },
                    new PatchTalkgroupMember { SystemName = "SYS-2", Tgid = "200" },
                });

            var begun = Assert.Single(harness.Begun);
            Assert.Equal(new[] { "200" }, begun.Select(target => target.TalkgroupId));
        }

        private static Codeplug MakeCodeplug()
            => new()
            {
                Systems = new List<Codeplug.System>
                {
                    new() { Name = "SYS-1", Rid = "1001" },
                    new() { Name = "SYS-2", Rid = "1002" },
                },
                Zones = new List<Codeplug.Zone>
                {
                    new()
                    {
                        Name = "Zone",
                        Channels = new List<Codeplug.Channel>
                        {
                            new() { Name = "Channel 1", System = "SYS-1", Tgid = "100", Slot = 1, Mode = "dmr" },
                            new() { Name = "Channel 2", System = "SYS-2", Tgid = "200", Slot = 2, Mode = "p25" },
                        },
                    },
                },
            };
    }
}
