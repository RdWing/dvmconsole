// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using DvmConsole.Avalonia.Services;
using DvmConsole.Platform.Audio;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    public sealed class ToneDispatchRuntimeCoordinatorLifecycleTests
    {
        [Fact]
        public async Task DisposeAsync_CancelsAndJoinsAnInFlightDispatchWithoutLateRouterCall()
        {
            var transmitStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var transmitEnded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var routerCalls = 0;
            var target = new TransmitTarget("SYS", "101", 1, VoiceMode.Dmr, 1001);

            await using var coordinator = new ToneDispatchRuntimeCoordinator(
                () => new[] { target },
                _ => true,
                () => false,
                async (_, _, _, cancellationToken) =>
                {
                    Interlocked.Increment(ref routerCalls);
                    transmitStarted.SetResult(true);
                    await transmitEnded.Task.WaitAsync(cancellationToken);
                });

            Task<bool> send = coordinator.SendGeneratedPcmAsync(
                new byte[AudioPcm.FrameBytes],
                sendStartSignal: false,
                CancellationToken.None);
            await transmitStarted.Task;

            await coordinator.DisposeAsync();
            Assert.False(await send);
            Assert.Equal(1, routerCalls);

            Assert.False(await coordinator.SendGeneratedPcmAsync(
                new byte[AudioPcm.FrameBytes],
                sendStartSignal: false,
                CancellationToken.None));
            Assert.Equal(1, routerCalls);

            await coordinator.DisposeAsync();
            transmitEnded.TrySetResult(true);
        }
    }
}
