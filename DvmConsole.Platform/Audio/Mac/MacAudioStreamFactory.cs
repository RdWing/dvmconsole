// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DvmConsole.Platform.Audio;

namespace DvmConsole.Platform.Audio.Mac
{
    /// <summary>
    /// Concrete macOS audio factory backed by the CoreAudio HAL and AudioQueue
    /// Services. Device identifiers are resolved against a fresh HAL snapshot so
    /// default-device and hot-plug changes do not leave stale native IDs behind.
    /// </summary>
    public sealed class MacAudioStreamFactory : IAudioStreamFactory
    {
        private readonly MacAudioDeviceCatalog _catalog;
        private readonly bool _ownsCatalog;
        private readonly object _streamsGate = new();
        private readonly List<IAsyncDisposable> _streams = new();
        private int _disposed;

        public MacAudioStreamFactory()
            : this(new MacAudioDeviceCatalog(), true)
        {
        }

        public MacAudioStreamFactory(MacAudioDeviceCatalog catalog)
            : this(catalog, false)
        {
        }

        private MacAudioStreamFactory(MacAudioDeviceCatalog catalog, bool ownsCatalog)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _ownsCatalog = ownsCatalog;
        }

        public IAudioInput CreateInput(AudioDeviceId deviceId, PcmFormat format)
        {
            ThrowIfDisposed();
            if (!_catalog.TryResolve(AudioDeviceDirection.Input, deviceId, out var descriptor) || descriptor is null)
            {
                throw new AudioDeviceException(
                    AudioDeviceErrorKind.DeviceUnavailable,
                    "The requested CoreAudio input device is not available.");
            }

            var input = new MacAudioInput(descriptor, format);
            Track(input);
            return input;
        }

        public IAudioOutput CreateOutput(AudioDeviceId deviceId, PcmFormat format)
        {
            ThrowIfDisposed();
            if (!_catalog.TryResolve(AudioDeviceDirection.Output, deviceId, out var descriptor) || descriptor is null)
            {
                throw new AudioDeviceException(
                    AudioDeviceErrorKind.DeviceUnavailable,
                    "The requested CoreAudio output device is not available.");
            }

            var output = new MacAudioOutput(descriptor, format);
            Track(output);
            return output;
        }

        public IAudioFilePlayer CreateFilePlayer()
        {
            ThrowIfDisposed();
            return new MacAudioFilePlayer(this);
        }

        public IAudioWaveFilePlayer CreateWaveFilePlayer()
        {
            ThrowIfDisposed();
            return new MacAudioWaveFilePlayer(this);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            IAsyncDisposable[] streams;
            lock (_streamsGate)
            {
                streams = _streams.ToArray();
                _streams.Clear();
            }

            foreach (var stream in streams)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            if (_ownsCatalog)
            {
                await _catalog.DisposeAsync().ConfigureAwait(false);
            }
        }

        internal void Track(IAsyncDisposable stream)
        {
            lock (_streamsGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    _ = stream.DisposeAsync();
                    throw new ObjectDisposedException(nameof(MacAudioStreamFactory));
                }

                _streams.Add(stream);
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(MacAudioStreamFactory));
            }
        }
    }

    /// <summary>
    /// Raw PCM file player that feeds the same bounded CoreAudio output path as
    /// live playback. The output is stopped in all terminal paths.
    /// </summary>
    internal sealed class MacAudioFilePlayer : IAudioFilePlayer
    {
        private readonly MacAudioStreamFactory _factory;
        private readonly object _stateGate = new();
        private CancellationTokenSource? _stopSource;
        private IAudioOutput? _output;

        internal MacAudioFilePlayer(MacAudioStreamFactory factory)
        {
            _factory = factory;
        }

        public async Task<AudioPlaybackResult> PlayPcmAsync(
            string filePath,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return new AudioPlaybackResult(AudioPlaybackOutcome.Failed, "A PCM file path is required.");
            }

            // File-open failures (missing or unreadable file) are user-facing,
            // not programmer errors: report them as typed failures without
            // disturbing the WAVE contract. Stream lifetime stays owned by the
            // delegated playback below, disposed when it completes or throws.
            FileStream stream;
            try
            {
                stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    AudioPcm.BlockBytes,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (IOException exception)
            {
                return new AudioPlaybackResult(AudioPlaybackOutcome.Failed, exception.Message);
            }
            catch (UnauthorizedAccessException exception)
            {
                return new AudioPlaybackResult(AudioPlaybackOutcome.Failed, exception.Message);
            }

            await using (stream)
            {
                return await PlayPcmStreamAsync(stream, cancellationToken).ConfigureAwait(false);
            }
        }

        internal async Task<AudioPlaybackResult> PlayPcmStreamAsync(
            Stream stream,
            CancellationToken cancellationToken)
        {
            if (stream is null)
            {
                return new AudioPlaybackResult(AudioPlaybackOutcome.Failed, "A PCM stream is required.");
            }

            CancellationTokenSource stopSource;
            lock (_stateGate)
            {
                if (_stopSource is not null)
                {
                    return new AudioPlaybackResult(AudioPlaybackOutcome.Failed, "PCM playback is already active.");
                }

                stopSource = new CancellationTokenSource();
                _stopSource = stopSource;
            }

            using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                stopSource.Token);
            var token = linkedSource.Token;

            try
            {
                var output = _factory.CreateOutput(AudioDeviceId.Default, AudioPcm.Console);
                lock (_stateGate)
                {
                    _output = output;
                }

                var buffer = new byte[AudioPcm.BlockBytes];
                while (true)
                {
                    var count = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                    if (count == 0)
                    {
                        break;
                    }

                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        var result = output.Write(buffer.AsMemory(0, count));
                        if (result.Status == AudioWriteStatus.Accepted)
                        {
                            break;
                        }

                        if (result.Status == AudioWriteStatus.DeviceLost || result.Status == AudioWriteStatus.NotStarted)
                        {
                            return new AudioPlaybackResult(AudioPlaybackOutcome.Failed, "The CoreAudio output device was lost during playback.");
                        }

                        await Task.Delay(5, token).ConfigureAwait(false);
                    }
                }

                var drainMilliseconds = (int)Math.Ceiling(
                    output.Format.BytesPerSecond == 0
                        ? 0
                        : output.Write(ReadOnlyMemory<byte>.Empty).BufferedBytes * 1000.0 / output.Format.BytesPerSecond);
                if (drainMilliseconds > 0)
                {
                    await Task.Delay(Math.Min(drainMilliseconds, 10000), token).ConfigureAwait(false);
                }

                return new AudioPlaybackResult(AudioPlaybackOutcome.Completed, null);
            }
            catch (OperationCanceledException)
            {
                return new AudioPlaybackResult(AudioPlaybackOutcome.Cancelled, null);
            }
            catch (AudioDeviceException exception)
            {
                return new AudioPlaybackResult(AudioPlaybackOutcome.Failed, exception.Message);
            }
            catch (IOException exception)
            {
                return new AudioPlaybackResult(AudioPlaybackOutcome.Failed, exception.Message);
            }
            finally
            {
                IAudioOutput? output;
                lock (_stateGate)
                {
                    output = _output;
                    _output = null;
                    _stopSource?.Dispose();
                    _stopSource = null;
                }

                if (output is not null)
                {
                    await output.StopAsync().ConfigureAwait(false);
                }
            }
        }

        public Task StopAsync()
        {
            CancellationTokenSource? stopSource;
            IAudioOutput? output;
            lock (_stateGate)
            {
                stopSource = _stopSource;
                output = _output;
            }

            stopSource?.Cancel();
            return output?.StopAsync() ?? Task.CompletedTask;
        }
    }
}
