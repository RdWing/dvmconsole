// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DvmConsole.Avalonia.Services;
using DvmConsole.Platform.Audio;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for Gate 5.5 generated alert/tone dispatch.
    /// The coordinator owns request-time guards and target snapshots; the
    /// router remains the transport/fan-out owner.
    /// </summary>
    public sealed class ToneDispatchRuntimeCoordinatorTests
    {
        [Fact]
        public async Task SendGeneratedPcm_UsesOnePressTimeSnapshot_AndPreservesMixedModeOrder()
        {
            var targetA = new TransmitTarget("SYS-A", "101", 1, VoiceMode.Dmr, 1001);
            var targetB = new TransmitTarget("SYS-B", "202", 2, VoiceMode.P25, 1002);
            IReadOnlyList<TransmitTarget> currentTargets = new[] { targetA, targetB };
            var sentTargets = new List<IReadOnlyList<TransmitTarget>>();
            var sentPcm = new List<byte[]>();
            var pcm = new byte[AudioPcm.FrameBytes * 2];

            await using var coordinator = new ToneDispatchRuntimeCoordinator(
                () => currentTargets,
                _ => true,
                () => false,
                (targets, payload, _, _) =>
                {
                    currentTargets = new[] { targetB };
                    sentTargets.Add(targets);
                    sentPcm.Add(payload.ToArray());
                    return Task.CompletedTask;
                });

            await coordinator.SendGeneratedPcmAsync(pcm, sendStartSignal: true, CancellationToken.None);

            var snapshot = Assert.Single(sentTargets);
            Assert.Equal(new[] { targetA, targetB }, snapshot);
            Assert.Equal(pcm, Assert.Single(sentPcm));
        }

        [Fact]
        public async Task SendGeneratedPcm_BlocksUnavailableTargetsAndActiveMicPttBeforeRouter()
        {
            var target = new TransmitTarget("SYS", "101", 1, VoiceMode.Dmr, 1001);
            var routerCalls = 0;
            var available = false;
            var micPttActive = false;
            var statuses = new List<string>();

            await using var coordinator = new ToneDispatchRuntimeCoordinator(
                () => new[] { target },
                _ => available,
                () => micPttActive,
                (_, _, _, _) =>
                {
                    routerCalls++;
                    return Task.CompletedTask;
                },
                statuses.Add);

            await coordinator.SendGeneratedPcmAsync(
                new byte[AudioPcm.FrameBytes],
                sendStartSignal: true,
                CancellationToken.None);

            Assert.Equal(0, routerCalls);
            Assert.Contains("unavailable", Assert.Single(statuses), StringComparison.OrdinalIgnoreCase);

            statuses.Clear();
            available = true;
            micPttActive = true;
            await coordinator.SendGeneratedPcmAsync(
                new byte[AudioPcm.FrameBytes],
                sendStartSignal: true,
                CancellationToken.None);

            Assert.Equal(0, routerCalls);
            Assert.Contains("PTT", Assert.Single(statuses), StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static class ToneDispatchTestExtensions
    {
        internal static bool All<T>(this IReadOnlyList<T> values, Func<T, bool> predicate)
        {
            for (var i = 0; i < values.Count; i++)
            {
                if (!predicate(values[i]))
                    return false;
            }

            return true;
        }
    }
}
