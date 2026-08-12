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
    /// <summary>
    /// Gate 5.2 RED contracts for preview ownership. The coordinator validates
    /// before playback and owns cancellation/stop sequencing for one preview.
    /// </summary>
    public sealed class AlertTonePreviewCoordinatorTests
    {
        [Fact]
        public async Task InvalidWave_DoesNotReachPlayer()
        {
            var inspector = new RecordingInspector(AudioWaveInspectionResult.Invalid("bad wave"));
            var player = new RecordingPlayer();
            await using var coordinator = new AlertTonePreviewCoordinator(inspector, player);

            AudioPlaybackResult result = await coordinator.PreviewAsync("bad.wav", CancellationToken.None);

            Assert.Equal(AudioPlaybackOutcome.Failed, result.Outcome);
            Assert.Equal(0, player.PlayCount);
        }

        [Fact]
        public async Task Stop_CancelsAndStopsActivePreview()
        {
            var inspector = new RecordingInspector(AudioWaveInspectionResult.Valid());
            var player = new RecordingPlayer();
            await using var coordinator = new AlertTonePreviewCoordinator(inspector, player);

            Task<AudioPlaybackResult> preview = coordinator.PreviewAsync("one.wav", CancellationToken.None);
            Assert.Equal(1, player.PlayCount);

            await coordinator.StopAsync();
            AudioPlaybackResult result = await preview;

            Assert.Equal(AudioPlaybackOutcome.Cancelled, result.Outcome);
            Assert.Equal(1, player.StopCount);
            Assert.True(player.LastToken.IsCancellationRequested);
        }

        [Fact]
        public async Task StartingAnotherPreview_StopsThePreviousSession()
        {
            var inspector = new RecordingInspector(AudioWaveInspectionResult.Valid());
            var player = new RecordingPlayer();
            await using var coordinator = new AlertTonePreviewCoordinator(inspector, player);

            Task<AudioPlaybackResult> first = coordinator.PreviewAsync("one.wav", CancellationToken.None);
            Task<AudioPlaybackResult> second = coordinator.PreviewAsync("two.wav", CancellationToken.None);

            Assert.Equal(2, player.PlayCount);
            Assert.Equal(1, player.StopCount);
            Assert.Equal(new[] { "play:one.wav", "stop", "play:two.wav" }, player.Operations);
            Assert.Equal(AudioPlaybackOutcome.Cancelled, (await first).Outcome);

            await coordinator.StopAsync();
            Assert.Equal(AudioPlaybackOutcome.Cancelled, (await second).Outcome);
        }

        [Fact]
        public async Task CallerCancellation_ReturnsTypedCancelledResult()
        {
            var inspector = new RecordingInspector(AudioWaveInspectionResult.Valid());
            var player = new RecordingPlayer();
            await using var coordinator = new AlertTonePreviewCoordinator(inspector, player);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            AudioPlaybackResult result = await coordinator.PreviewAsync(
                "cancelled.wav",
                cancellation.Token);

            Assert.Equal(AudioPlaybackOutcome.Cancelled, result.Outcome);
            Assert.Equal(0, player.PlayCount);
        }

        private sealed class RecordingInspector : IAudioWaveFileInspector
        {
            private readonly AudioWaveInspectionResult result;

            public RecordingInspector(AudioWaveInspectionResult result)
            {
                this.result = result;
            }

            public AudioWaveInspectionResult Inspect(string path) => result;
        }

        private sealed class RecordingPlayer : IAudioWaveFilePlayer
        {
            private readonly object gate = new();
            private TaskCompletionSource<AudioPlaybackResult> completion = NewCompletion();

            public int PlayCount { get; private set; }
            public int StopCount { get; private set; }
            public CancellationToken LastToken { get; private set; }
            public System.Collections.Generic.List<string> Operations { get; } = new();

            public Task<AudioPlaybackResult> PlayWavAsync(
                string filePath,
                CancellationToken cancellationToken)
            {
                lock (gate)
                {
                    PlayCount++;
                    LastToken = cancellationToken;
                    Operations.Add("play:" + filePath);
                    completion = NewCompletion();
                    cancellationToken.Register(() => completion.TrySetResult(
                        new AudioPlaybackResult(AudioPlaybackOutcome.Cancelled, null)));
                    return completion.Task;
                }
            }

            public Task StopAsync()
            {
                lock (gate)
                {
                    StopCount++;
                    Operations.Add("stop");
                    completion.TrySetResult(
                        new AudioPlaybackResult(AudioPlaybackOutcome.Cancelled, null));
                    return Task.CompletedTask;
                }
            }

            private static TaskCompletionSource<AudioPlaybackResult> NewCompletion()
                => new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
