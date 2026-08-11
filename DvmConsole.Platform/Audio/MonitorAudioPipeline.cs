// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;

namespace DvmConsole.Platform.Audio
{
    /// <summary>
    /// Receive-side monitor audio pipeline. Owns one playback stream created
    /// through an injected <see cref="IAudioStreamFactory"/> at
    /// <see cref="Start"/> (console PCM format), forwards PCM writes to it,
    /// sheds the oldest backlog when the buffered audio exceeds a bounded
    /// duration (MacAudioBufferPolicy semantics: shed oldest, keep newest),
    /// optionally decodes 20 ms voice frames into PCM through an injected
    /// <see cref="IVoiceFrameDecoder"/> seam, clamps and forwards volume, and
    /// surfaces device loss through a single <see cref="StreamEnded"/> event
    /// raised from the write status. The pipeline is Dispatcher-free: callers
    /// marshal events to their UI thread.
    /// </summary>
    public sealed class MonitorAudioPipeline : IAsyncDisposable
    {
        /// <summary>
        /// Default backlog shed threshold: 250 ms of buffered audio, matching
        /// the WPF live-monitor behavior (AudioManager.AddLiveMonitorStream,
        /// dvmconsole/AudioManager.cs:75-89).
        /// </summary>
        private static readonly TimeSpan DefaultMaxBufferedDuration = TimeSpan.FromMilliseconds(250);
        private const float MaxVolume = 4.0f;

        private readonly IAudioStreamFactory _factory;
        private readonly IVoiceFrameDecoder? _decoder;
        private readonly int _maxBufferedBytes;
        private readonly object _stateGate = new();
        private IAudioOutput? _output;
        private float _volume = 1.0f;
        private int _bufferedBytes;
        private int _started;
        private int _stopped;
        private int _deviceLost;
        private int _streamEndedRaised;
        private int _disposed;

        /// <summary>
        /// Creates a monitor pipeline over the given stream factory.
        /// </summary>
        /// <param name="factory">Factory that creates the playback stream at Start.</param>
        /// <param name="decoder">
        /// Optional voice-frame decoder seam; required for
        /// <see cref="WriteVoiceFrame"/>.
        /// </param>
        /// <param name="maxBufferedDuration">
        /// Maximum audio backlog before the oldest buffered data is shed. The
        /// default is 250 ms (WPF live-monitor parity).
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="factory"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="maxBufferedDuration"/> is not positive.
        /// </exception>
        public MonitorAudioPipeline(
            IAudioStreamFactory factory,
            IVoiceFrameDecoder? decoder = null,
            TimeSpan? maxBufferedDuration = null)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _decoder = decoder;

            var duration = maxBufferedDuration ?? DefaultMaxBufferedDuration;
            if (duration <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxBufferedDuration), maxBufferedDuration,
                    "The maximum buffered duration must be positive.");
            }

            // Shed threshold in buffered bytes: the console byte rate
            // (BytesPerSecond = 16000) times the duration, so 100 ms caps
            // the backlog at 1600 bytes (the locked shed contract), 250 ms
            // at 4000 bytes.
            _maxBufferedBytes = (int)(AudioPcm.Console.BytesPerSecond * duration.TotalSeconds);
        }

        /// <summary>
        /// True after a successful <see cref="Start"/> and until
        /// <see cref="StopAsync"/> or <see cref="DisposeAsync"/>.
        /// </summary>
        public bool IsRunning => Volatile.Read(ref _started) != 0 && Volatile.Read(ref _stopped) == 0;

        /// <summary>
        /// Raised exactly once when a write reports
        /// <see cref="AudioWriteStatus.DeviceLost"/>; the pipeline then enters
        /// a lost state and subsequent writes report DeviceLost without
        /// re-raising.
        /// </summary>
        public event Action<AudioStreamEnd>? StreamEnded;

        /// <summary>
        /// Creates the playback stream for the requested device and starts
        /// the pipeline.
        /// </summary>
        /// <param name="outputDeviceId">Device to play monitor audio on.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the pipeline is already started (single-start, parity
        /// MacAudioInput).
        /// </exception>
        /// <exception cref="AudioDeviceException">
        /// Thrown, typed, when the output device is unavailable.
        /// </exception>
        public void Start(AudioDeviceId outputDeviceId)
        {
            lock (_stateGate)
            {
                if (Volatile.Read(ref _started) != 0)
                {
                    throw new InvalidOperationException(
                        "The monitor audio pipeline can only be started once.");
                }

                var output = _factory.CreateOutput(outputDeviceId, AudioPcm.Console);
                _output = output;
                Volatile.Write(ref _started, 1);
                output.Volume = _volume;
            }
        }

        /// <summary>
        /// Writes raw PCM to the playback stream, shedding the oldest backlog
        /// when the buffered audio exceeds the maximum buffered duration.
        /// </summary>
        /// <returns>The playback stream's write result.</returns>
        public AudioWriteResult WritePcm(ReadOnlyMemory<byte> pcm)
        {
            if (Volatile.Read(ref _deviceLost) != 0)
            {
                return new AudioWriteResult(AudioWriteStatus.DeviceLost, _bufferedBytes);
            }

            var output = Volatile.Read(ref _output);
            if (output is null || Volatile.Read(ref _stopped) != 0)
            {
                return new AudioWriteResult(AudioWriteStatus.NotStarted, 0);
            }

            var result = output.Write(pcm);
            _bufferedBytes = result.BufferedBytes;

            if (result.Status == AudioWriteStatus.DeviceLost)
            {
                Volatile.Write(ref _deviceLost, 1);
                RaiseStreamEndedOnce();
                return result;
            }

            if (result.Status == AudioWriteStatus.Accepted && result.BufferedBytes > _maxBufferedBytes)
            {
                // Shed the oldest backlog and keep the newest write's bytes
                // tracked: clear the output buffer, then re-write the newest
                // payload so the most recent audio stays queued for playback
                // (WPF live-monitor parity: ClearBuffer then add).
                output.ClearBuffer();
                result = output.Write(pcm);
                _bufferedBytes = result.BufferedBytes;

                if (result.Status == AudioWriteStatus.DeviceLost)
                {
                    // The device vanished between the first write and the
                    // shed re-write: enter the lost state exactly like the
                    // primary-write path so StreamEnded is raised once and
                    // subsequent writes report DeviceLost without re-raising.
                    Volatile.Write(ref _deviceLost, 1);
                    RaiseStreamEndedOnce();
                }
            }

            return result;
        }

        /// <summary>
        /// Decodes one 20 ms voice frame through the injected decoder seam and
        /// writes the resulting 160 16-bit PCM samples (320 bytes little-endian)
        /// to the playback stream. A frame the decoder rejects is silently
        /// skipped: no write occurs, the buffered byte count is unchanged and
        /// the pipeline stays healthy.
        /// </summary>
        /// <returns>
        /// The playback stream's write result, or Accepted with the unchanged
        /// buffered byte count when the frame could not be decoded.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no decoder was injected.
        /// </exception>
        public AudioWriteResult WriteVoiceFrame(ReadOnlyMemory<byte> voiceFrame)
        {
            if (_decoder is null)
            {
                throw new InvalidOperationException(
                    "A voice-frame decoder must be injected to write voice frames.");
            }

            if (!_decoder.TryDecode(voiceFrame, out var samples))
            {
                return new AudioWriteResult(AudioWriteStatus.Accepted, _bufferedBytes);
            }

            var pcm = new byte[samples.Length * 2];
            for (var i = 0; i < samples.Length; i++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), samples[i]);
            }

            return WritePcm(pcm);
        }

        /// <summary>
        /// Playback volume, clamped to the WPF-compatible 0..4 range and
        /// forwarded to the playback stream.
        /// </summary>
        public float Volume
        {
            get => _volume;
            set
            {
                var clamped = float.IsNaN(value) || float.IsInfinity(value)
                    ? 1.0f
                    : Math.Clamp(value, 0.0f, MaxVolume);
                _volume = clamped;
                if (Volatile.Read(ref _output) is { } output)
                {
                    output.Volume = clamped;
                }
            }
        }

        /// <summary>
        /// Stops the playback stream. Idempotent; writes after stop report
        /// <see cref="AudioWriteStatus.NotStarted"/>.
        /// </summary>
        public Task StopAsync()
        {
            Volatile.Write(ref _stopped, 1);
            return Volatile.Read(ref _output)?.StopAsync() ?? Task.CompletedTask;
        }

        /// <summary>
        /// Stops and disposes the playback stream and disposes the injected
        /// factory. Idempotent; no further events are raised.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Volatile.Write(ref _stopped, 1);
            if (Volatile.Read(ref _output) is { } output)
            {
                await output.StopAsync().ConfigureAwait(false);
                if (output is IAsyncDisposable disposable)
                {
                    await disposable.DisposeAsync().ConfigureAwait(false);
                }
            }

            await _factory.DisposeAsync().ConfigureAwait(false);
        }

        private void RaiseStreamEndedOnce()
        {
            if (Interlocked.Exchange(ref _streamEndedRaised, 1) == 0)
            {
                StreamEnded?.Invoke(AudioStreamEnd.DeviceLost());
            }
        }
    }
}
