// SPDX-License-Identifier: AGPL-3.0-only
/**
* Digital Voice Modem - Desktop Dispatch Console
* AGPLv3 Open Source. Use is subject to license terms.
* DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
*
* @package DVM / Desktop Dispatch Console
* @license AGPLv3 License (https://opensource.org/licenses/AGPL-3.0)
*
*   Copyright (C) 2025 Caleb, K4PHP and DVMProject (https://github.com/dvmproject) Authors
*
*/

using System;

namespace dvmconsole
{
    /// <summary>
    /// Decoder-independent web-stream PCM playback session (Core-only).
    /// WPF parity: mirrors the session lifecycle, status strings, volume
    /// normalization, and audio-activity detection of the WPF
    /// <c>WebStreamChip</c> (dvmconsole/Controls/WebStreamChip.xaml.cs):
    /// status transitions "Off"/"Connecting"/"Retry {n}/3"/"Idle"/"Down"/"RX"
    /// (lines 124, 154, 157, 159, 176, 293), <c>NormalizeVolume</c>
    /// (lines 548-552, 0.1 steps clamped to 0.0..4.0), <c>IsAudioActive</c>
    /// (lines 438-463, 16-bit little-endian samples, peak >= 650 OR
    /// RMS >= 0.0035), the 1400 ms activity hold (line 43), and the
    /// <c>GenerateStreamId</c> nonzero uint (lines 482-487). Parity is
    /// predicate-level: the WPF UI idle timer's 200 ms polling margin is
    /// deliberately deferred (see remarks).
    /// </summary>
    /// <remarks>
    /// SESSION STATE ONLY. This seam deliberately performs no network I/O, no
    /// HTTP, no media decoding or resampling, and holds no Platform/UI
    /// references, events, or settings: the caller owns the byte transport
    /// (web fetch, decoder, resampler) and feeds decoded PCM here, and the
    /// <see cref="AppendPcm"/> sink hands the PCM to the caller's playback
    /// output. Deferred seams (documented for later gates): network/HTTP
    /// acquisition with connection retries, media decoding/resampling,
    /// Platform audio ownership (output device, volume application, playback
    /// stop), change notification events, and persisted settings.
    /// <para>
    /// Activity semantics (WPF <c>WebStreamChip.UpdateActivityState</c>,
    /// WebStreamChip.xaml.cs:426-436): every appended PCM buffer is
    /// classified for audio activity; an active buffer refreshes
    /// <c>lastAudioActivityUtc</c>, and <see cref="IsReceiving"/> is then
    /// recomputed per buffer as
    /// <c>now - lastAudioActivityUtc &lt;= <see cref="AudioActivityHold"/></c>
    /// (1400 ms, WPF line 43). A silent buffer arriving mid-hold therefore
    /// keeps the RX indicator lit, while a silent buffer from idle stays
    /// Idle; with no further PCM, the injected clock expires the hold via
    /// <see cref="Tick"/>.
    /// </para>
    /// <para>
    /// Deliberate headless normalization of the WPF UI idle timer: WPF
    /// <c>WebStreamChip</c> also runs a 250 ms <c>DispatcherTimer</c>
    /// (WebStreamChip.xaml.cs:68-77) that clears <c>IsReceiving</c> only
    /// when <c>now - lastAudioActivityUtc &gt; <see cref="AudioActivityHold"/>
    /// + 200 ms</c> (about 1600 ms effective display expiry, WPF line 74).
    /// This Core seam has no UI polling requirement and deliberately does
    /// not reproduce that deferred UI-polling detail: the injected clock
    /// and caller-driven <see cref="Tick"/> are the deterministic
    /// boundary, expiring the hold at &gt; 1400 ms. The WPF parity remarks
    /// above therefore claim activity-predicate equivalence, not
    /// timer-level equivalence.
    /// </para>
    /// </remarks>
    public sealed class WebStreamPlaybackSession
    {
        // WPF parity constants (dvmconsole/Controls/WebStreamChip.xaml.cs:37-43).
        private const int MaxConnectionAttempts = 3;
        private const double VolumeStep = 0.1;
        private const double AudioActivityRmsThreshold = 0.0035;
        private const short AudioActivityPeakThreshold = 650;
        private static readonly TimeSpan AudioActivityHold = TimeSpan.FromMilliseconds(1400);

        private readonly Action<uint, ReadOnlyMemory<byte>, double> _pcmSink;
        private readonly Func<DateTime> _utcNow;

        private bool _active;
        private double _volume = 1.0;
        private DateTime _lastAudioActivityUtc;

        /// <summary>
        /// Creates a web-stream playback session.
        /// </summary>
        /// <param name="pcmSink">
        /// Receives every appended PCM buffer (owned <see cref="StreamId"/>,
        /// a read-only view of the PCM bytes, and the current
        /// <see cref="Volume"/>). Required; null throws
        /// <see cref="ArgumentNullException"/>.
        /// </param>
        /// <param name="utcNow">
        /// Injectable clock for the activity hold; defaults to
        /// <see cref="DateTime.UtcNow"/>.
        /// </param>
        public WebStreamPlaybackSession(
            Action<uint, ReadOnlyMemory<byte>, double> pcmSink,
            Func<DateTime> utcNow = null)
        {
            if (pcmSink == null)
                throw new ArgumentNullException(nameof(pcmSink));

            _pcmSink = pcmSink;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>
        /// Current session status text: "Off" (stopped), "Connecting"
        /// (started, not yet connected), "Retry {n}/3", "Idle" (connected or
        /// activity expired), "RX" (receiving active PCM), "Down" (failed).
        /// </summary>
        public string StatusText { get; private set; } = "Off";

        /// <summary>
        /// The session-owned stream id (nonzero while active, 0 when stopped).
        /// </summary>
        public uint StreamId { get; private set; }

        /// <summary>
        /// True while the 1400 ms activity hold is unexpired: refreshed by
        /// an active PCM buffer and preserved by a silent buffer arriving
        /// mid-hold (see the class remarks); cleared by a silent buffer
        /// outside the hold, by <see cref="Tick"/> expiry, or by
        /// stop/failure.
        /// </summary>
        public bool IsReceiving { get; private set; }

        /// <summary>
        /// Playback volume, normalized like the WPF <c>WebStreamChip</c>:
        /// rounded to 0.1 steps and clamped to 0.0..4.0 (default 1.0).
        /// </summary>
        public double Volume
        {
            get => _volume;
            set => _volume = NormalizeVolume(value);
        }

        /// <summary>
        /// Starts the session: generates a nonzero owned stream id, marks the
        /// session active, clears receiving state, and sets the status to
        /// "Connecting". No-op (preserving stream id and status) when the
        /// session is already active.
        /// </summary>
        public void Start()
        {
            if (_active)
                return;

            StreamId = GenerateStreamId();
            _active = true;
            IsReceiving = false;
            StatusText = "Connecting";
        }

        /// <summary>
        /// Marks the connection established: an active session becomes
        /// "Idle". Safe no-op when stopped.
        /// </summary>
        public void MarkConnected()
        {
            if (!_active)
                return;

            StatusText = "Idle";
        }

        /// <summary>
        /// Marks a connection retry: an active session shows the exact
        /// status "Retry {attempt}/3". Safe no-op when stopped.
        /// </summary>
        /// <param name="attempt">The 1-based retry attempt number.</param>
        public void MarkRetry(int attempt)
        {
            if (!_active)
                return;

            StatusText = $"Retry {attempt}/{MaxConnectionAttempts}";
        }

        /// <summary>
        /// Marks the session failed: an active session shows the exact status
        /// "Down" and clears receiving. Safe no-op when stopped.
        /// </summary>
        public void MarkFailed()
        {
            if (!_active)
                return;

            IsReceiving = false;
            StatusText = "Down";
        }

        /// <summary>
        /// Appends decoded 16-bit little-endian PCM. Safe no-op when stopped,
        /// null, or empty. While active, the buffer is always forwarded to the
        /// PCM sink (owned stream id, read-only memory view, current volume).
        /// WPF-parity audio activity (peak &gt;= 650 OR RMS &gt;= 0.0035)
        /// refreshes the activity timestamp, then <see cref="IsReceiving"/> and
        /// the status are recomputed from the 1400 ms hold (WPF
        /// <c>UpdateActivityState</c>, WebStreamChip.xaml.cs:426-436): an
        /// active buffer, or a silent buffer arriving mid-hold, shows "RX"; a
        /// silent buffer outside the hold returns to "Idle".
        /// </summary>
        /// <param name="pcm">Signed 16-bit little-endian mono PCM bytes.</param>
        public void AppendPcm(byte[] pcm)
        {
            if (!_active || pcm == null || pcm.Length == 0)
                return;

            bool frameActive = IsAudioActive(pcm);
            _pcmSink(StreamId, pcm, Volume);

            DateTime now = _utcNow();
            if (frameActive)
                _lastAudioActivityUtc = now;

            bool inActivityHold = now - _lastAudioActivityUtc <= AudioActivityHold;
            IsReceiving = inActivityHold;
            StatusText = inActivityHold ? "RX" : "Idle";
        }

        /// <summary>
        /// Advances the injected clock: expires the activity hold (active
        /// receiving lasts 1400 ms; an expired hold clears receiving and
        /// returns an active session to "Idle"). A stopped session remains
        /// "Off".
        /// <para>
        /// This is the deterministic caller-driven boundary that replaces
        /// the WPF UI idle timer: WPF <c>WebStreamChip</c> polls the hold
        /// plus a 200 ms margin on a 250 ms <c>DispatcherTimer</c>, whereas
        /// here <c>Tick</c> expires the hold exactly at &gt; 1400 ms (see
        /// the class remarks on headless normalization).
        /// </para>
        /// </summary>
        public void Tick()
        {
            if (!_active || !IsReceiving)
                return;

            if (_utcNow() - _lastAudioActivityUtc > AudioActivityHold)
            {
                IsReceiving = false;
                StatusText = "Idle";
            }
        }

        /// <summary>
        /// Stops the session: clears active/receiving/failure state, resets
        /// <see cref="StreamId"/> to 0, and sets the status to "Off".
        /// Idempotent.
        /// </summary>
        public void Stop()
        {
            _active = false;
            IsReceiving = false;
            StreamId = 0;
            StatusText = "Off";
        }

        /// <summary>
        /// WPF parity (WebStreamChip.xaml.cs:548-552): 0.1-step rounding,
        /// clamped to 0.0..4.0.
        /// </summary>
        private static double NormalizeVolume(double value)
        {
            double steppedValue = Math.Round(value / VolumeStep) * VolumeStep;
            return Math.Max(0.0, Math.Min(4.0, steppedValue));
        }

        /// <summary>
        /// WPF parity (WebStreamChip.xaml.cs:438-463): signed 16-bit
        /// little-endian samples; active when the absolute peak is &gt;= 650
        /// or the RMS (normalized to full scale) is &gt;= 0.0035.
        /// </summary>
        private static bool IsAudioActive(byte[] buffer)
        {
            if (buffer == null || buffer.Length < 2)
                return false;

            int peak = 0;
            double sumSquares = 0.0;
            int sampleCount = 0;
            for (int i = 0; i + 1 < buffer.Length; i += 2)
            {
                short sample = (short)((buffer[i + 1] << 8) | buffer[i]);
                int abs = Math.Abs((int)sample);
                if (abs > peak)
                    peak = abs;

                double normalized = sample / 32768.0;
                sumSquares += normalized * normalized;
                sampleCount++;
            }

            if (sampleCount == 0)
                return false;

            double rms = Math.Sqrt(sumSquares / sampleCount);
            return rms >= AudioActivityRmsThreshold || peak >= AudioActivityPeakThreshold;
        }

        /// <summary>
        /// WPF parity (WebStreamChip.xaml.cs:482-487): random nonzero uint
        /// stream id.
        /// </summary>
        private static uint GenerateStreamId()
        {
            byte[] bytes = Guid.NewGuid().ToByteArray();
            uint streamId = BitConverter.ToUInt32(bytes, 0);
            return streamId == 0 ? 1 : streamId;
        }
    }
}
