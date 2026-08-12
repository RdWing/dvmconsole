// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using DvmConsole.Platform.Audio;

namespace DvmConsole.Avalonia.Services
{
    /// <summary>
    /// Owns one alert-tone preview session. Validation happens before the
    /// player is called; starting or stopping a preview cancels the previous
    /// session and waits for its player cleanup before proceeding.
    /// </summary>
    public sealed class AlertTonePreviewCoordinator : IAsyncDisposable
    {
        private readonly IAudioWaveFileInspector inspector;
        private readonly IAudioWaveFilePlayer player;
        private readonly SemaphoreSlim transitionGate = new(1, 1);
        private readonly object stateGate = new();
        private CancellationTokenSource? activeCancellation;
        private Task<AudioPlaybackResult>? activePlayback;
        private bool disposed;

        public AlertTonePreviewCoordinator(
            IAudioWaveFileInspector inspector,
            IAudioWaveFilePlayer player)
        {
            this.inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
            this.player = player ?? throw new ArgumentNullException(nameof(player));
        }

        public async Task<AudioPlaybackResult> PreviewAsync(
            string filePath,
            CancellationToken cancellationToken)
        {
            AudioWaveInspectionResult inspection;
            try
            {
                inspection = inspector.Inspect(filePath);
            }
            catch (Exception exception)
            {
                return Failed(exception.Message);
            }

            if (!inspection.IsValid)
                return Failed(inspection.ErrorMessage ?? "The WAVE file is invalid.");

            try
            {
                await transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new AudioPlaybackResult(AudioPlaybackOutcome.Cancelled, null);
            }

            Task<AudioPlaybackResult> playback;
            CancellationTokenSource currentCancellation;
            try
            {
                ThrowIfDisposed();

                CancellationTokenSource? previousCancellation;
                Task<AudioPlaybackResult>? previousPlayback;
                lock (stateGate)
                {
                    previousCancellation = activeCancellation;
                    previousPlayback = activePlayback;
                    activeCancellation = null;
                    activePlayback = null;
                }

                previousCancellation?.Cancel();
                if (previousCancellation is not null)
                    await player.StopAsync().ConfigureAwait(false);
                if (previousPlayback is not null)
                    await IgnorePlaybackFailureAsync(previousPlayback).ConfigureAwait(false);
                previousCancellation?.Dispose();

                currentCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                try
                {
                    playback = player.PlayWavAsync(filePath, currentCancellation.Token);
                }
                catch
                {
                    currentCancellation.Dispose();
                    throw;
                }

                lock (stateGate)
                {
                    activeCancellation = currentCancellation;
                    activePlayback = playback;
                }
            }
            catch (OperationCanceledException)
            {
                return new AudioPlaybackResult(AudioPlaybackOutcome.Cancelled, null);
            }
            catch (Exception exception)
            {
                return Failed(exception.Message);
            }
            finally
            {
                transitionGate.Release();
            }

            return await AwaitPlaybackAsync(playback, currentCancellation).ConfigureAwait(false);
        }

        public async Task StopAsync()
        {
            await transitionGate.WaitAsync().ConfigureAwait(false);
            try
            {
                CancellationTokenSource? cancellation;
                Task<AudioPlaybackResult>? playback;
                lock (stateGate)
                {
                    cancellation = activeCancellation;
                    playback = activePlayback;
                    activeCancellation = null;
                    activePlayback = null;
                }

                cancellation?.Cancel();
                await player.StopAsync().ConfigureAwait(false);
                if (playback is not null)
                    await IgnorePlaybackFailureAsync(playback).ConfigureAwait(false);
                cancellation?.Dispose();
            }
            finally
            {
                transitionGate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await transitionGate.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (stateGate)
                {
                    if (disposed)
                        return;
                    disposed = true;
                }

                CancellationTokenSource? cancellation;
                Task<AudioPlaybackResult>? playback;
                lock (stateGate)
                {
                    cancellation = activeCancellation;
                    playback = activePlayback;
                    activeCancellation = null;
                    activePlayback = null;
                }

                cancellation?.Cancel();
                await player.StopAsync().ConfigureAwait(false);
                if (playback is not null)
                    await IgnorePlaybackFailureAsync(playback).ConfigureAwait(false);
                cancellation?.Dispose();
            }
            finally
            {
                transitionGate.Release();
            }
        }

        private async Task<AudioPlaybackResult> AwaitPlaybackAsync(
            Task<AudioPlaybackResult> playback,
            CancellationTokenSource cancellation)
        {
            AudioPlaybackResult result;
            try
            {
                result = await playback.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = new AudioPlaybackResult(AudioPlaybackOutcome.Cancelled, null);
            }
            catch (Exception exception)
            {
                result = Failed(exception.Message);
            }

            bool ownsCancellation;
            lock (stateGate)
            {
                ownsCancellation = ReferenceEquals(activePlayback, playback);
                if (ownsCancellation)
                {
                    activePlayback = null;
                    activeCancellation = null;
                }
            }
            if (ownsCancellation)
                cancellation.Dispose();

            return result;
        }

        private static async Task IgnorePlaybackFailureAsync(Task<AudioPlaybackResult> playback)
        {
            try
            {
                await playback.ConfigureAwait(false);
            }
            catch
            {
                // A replaced or stopped preview must not propagate its provider error.
            }
        }

        private void ThrowIfDisposed()
        {
            lock (stateGate)
            {
                if (disposed)
                    throw new ObjectDisposedException(nameof(AlertTonePreviewCoordinator));
            }
        }

        private static AudioPlaybackResult Failed(string message)
            => new(AudioPlaybackOutcome.Failed, string.IsNullOrWhiteSpace(message)
                ? "Alert-tone preview failed."
                : message);
    }
}
