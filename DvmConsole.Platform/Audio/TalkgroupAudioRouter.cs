// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DvmConsole.Platform.Audio
{
    /// <summary>
    /// Headless talkgroup audio router: the receive/transmit audio
    /// engine of the console. Receive side routes per-talkgroup
    /// <see cref="MonitorAudioPipeline"/> instances (lazy creation,
    /// WPF AudioManager parity 250 ms shed via the pipeline, 2 s idle
    /// release), decodes each frame PER-CODEWORD through the injected
    /// <see cref="IVoiceFrameDecoder"/>, and sends each successful decoded
    /// PCM frame to the optional <see cref="IDecodedPcmObserver"/> before
    /// optional local-monitor playback (a DMR 27-byte frame yields three
    /// 9-byte codeword decodes of 320 PCM bytes, a P25 225-byte LDU nine
    /// 11-byte codeword decodes — MainWindow.DMR.cs:182-203,
    /// MainWindow.P25.cs:301-333). Transmit side gates one
    /// <see cref="CaptureAudioPipeline"/> per PTT, splits each 1600-byte
    /// capture block into five 320-byte chunks (AudioConverter.
    /// SplitToChunks parity), encodes each chunk PER-CODEWORD through
    /// the injected <see cref="IVoiceFrameEncoder"/> — once per unique
    /// voice mode, fanned out to every target entry of that mode (the
    /// AllChannels PTT fan-out; one capture serves all targets, each
    /// with independent stream/seq counters) — accumulates DMR
    /// triples (3 x 9 bytes) and P25 LDUs (9 x 11 bytes) and delivers
    /// complete units through the injected <see cref="IVoiceTrafficSender"/>,
    /// while looping the raw PCM back to a local-monitor output with the
    /// WPF 250 ms shed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ownership: the router OWNS the injected
    /// <see cref="IAudioStreamFactory"/> and disposes it exactly once on
    /// its own <see cref="DisposeAsync"/>. Per-talkgroup pipelines are
    /// STOPPED, never individually disposed — their DisposeAsync would
    /// dispose the shared factory.
    /// </para>
    /// <para>
    /// Thread-safety: <see cref="RouteVoiceFrame"/> may be called from
    /// any thread (per-talkgroup state is guarded); transmit lifecycle
    /// (<see cref="BeginTransmitAsync"/>, <see cref="EndTransmitAsync"/>)
    /// is serialized on its own gate; the capture pump callback runs on
    /// the capture thread and only touches per-session accumulator state.
    /// </para>
    /// </remarks>
    public sealed class TalkgroupAudioRouter : IAsyncDisposable
    {
        /// <summary>
        /// Default idle release delay before a silent talkgroup's monitor
        /// pipeline is stopped (WPF parity, dvmconsole/AudioManager.cs:36).
        /// </summary>
        private static readonly TimeSpan DefaultIdleReleaseDelay = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Cancels a one-shot scheduler handle by disposing its timer.
        /// </summary>
        private sealed class TimerCancellation : IDisposable
        {
            private readonly Timer timer;

            public TimerCancellation(Timer timer) => this.timer = timer;

            public void Dispose() => timer.Dispose();
        }

        /// <summary>
        /// Per-talkgroup receive state. Guarded by <see cref="_routeGate"/>.
        /// </summary>
        private sealed class TalkgroupState
        {
            public MonitorAudioPipeline? Pipeline;
            public IDisposable? ReleaseHandle;
            public VoiceMode Mode;
            public bool MonitorUnavailableNotified;
        }

        /// <summary>
        /// Per-target transmit state within an active transmit session.
        /// Ordered: entries are processed in the order the targets were
        /// supplied to
        /// <see cref="BeginTransmitAsync(IReadOnlyList{TransmitTarget}, AudioDeviceId, CancellationToken)"/>,
        /// so a capture block fans out one complete unit per target, in
        /// target order. The pending-codeword list is only touched by
        /// the capture pump thread; the stream id and sequence counters
        /// are per-target, so every target's frames carry independent
        /// stream/seq numbering.
        /// </summary>
        private sealed class TargetEntry
        {
            public TargetEntry(TransmitTarget target, int codewordsPerUnit)
            {
                Target = target;
                CodewordsPerUnit = codewordsPerUnit;
            }

            public TransmitTarget Target { get; }

            /// <summary>Codewords per complete unit: 3 for DMR, 9 for P25.</summary>
            public int CodewordsPerUnit { get; }

            /// <summary>Pending codewords awaiting a complete unit.</summary>
            public List<byte[]> PendingCodewords { get; } = new List<byte[]>();

            /// <summary>Monotonically increasing per-frame stream id.</summary>
            public uint StreamIdCounter;

            /// <summary>Per-frame sequence number within this transmit.</summary>
            public int SeqNo;
        }

        /// <summary>
        /// Active transmit session state. Guarded by
        /// <see cref="_transmitGate"/>; the per-target pending-codeword
        /// lists are only touched by the capture pump thread.
        /// </summary>
        private sealed class TransmitSession
        {
            public TransmitSession(AudioDeviceId inputDeviceId, IReadOnlyList<TargetEntry> targets)
            {
                InputDeviceId = inputDeviceId;
                Targets = targets;
            }

            /// <summary>Input device used by this transmit session.</summary>
            public AudioDeviceId InputDeviceId { get; }

            /// <summary>
            /// Ordered per-target transmit state; one entry per target
            /// of the session, retained across capture restarts so a HAL
            /// unplug does not split the logical transmission.
            /// </summary>
            public IReadOnlyList<TargetEntry> Targets { get; }

            /// <summary>
            /// Set under the transmit gate when a deliberate end was
            /// requested for this session; read by the end-task observer so
            /// a deliberate end is never surfaced as an unexpected capture
            /// end. Per-session: a quick re-begin must not reset the prior
            /// session's end state.
            /// </summary>
            public volatile bool EndRequested;

            /// <summary>
            /// Set when this session's transmit ended; the capture pump
            /// sends nothing after this (a late block on a prior session's
            /// pump after a re-begin is a no-op).
            /// </summary>
            public volatile bool TransmitEnded;

            /// <summary>0/1 — whether CaptureEnded was raised for this session.</summary>
            public int CaptureEndedRaised;

            /// <summary>
            /// Set when a DeviceLost end needs a device-change notification
            /// to restart the capture without ending the transmit session.
            /// Volatile: set from the capture-end observer outside the
            /// transmit gate, read under it.
            /// </summary>
            public volatile bool RestartPending;

            /// <summary>
            /// 0/1 — set once the capture pump delivered a block for this
            /// session (the stream actually ran). A Requested end for a
            /// session whose stream never delivered a block ended as it
            /// began — a start artifact, not a mid-stream anomaly — and is
            /// never surfaced.
            /// </summary>
            public int PumpedBlocks;
        }

        private readonly IAudioStreamFactory _factory;
        private readonly IVoiceFrameDecoder _decoder;
        private readonly IVoiceFrameEncoder _encoder;
        private readonly IVoiceTrafficSender _sender;
        private readonly Func<AudioDeviceId> _resolveOutputDevice;
        private readonly Func<string, bool> _resolveMonitorEnabled;
        private readonly Func<string, bool> _resolveSpeakerOutputEnabled;
        private readonly Func<string, AudioDeviceId> _resolveTalkgroupOutputDevice;
        private readonly Func<string, float> _resolveTalkgroupVolume;
        private readonly IDecodedPcmObserver? _decodedPcmObserver;
        private readonly ITransmittedPcmObserver? _transmittedPcmObserver;
        private readonly TimeSpan _idleReleaseDelay;
        private readonly Func<TimeSpan, Action, IDisposable> _scheduler;
        private readonly Func<DateTime> _clock;

        private static readonly TimeSpan CaptureRestartThrottle = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Number of P25 TDUs emitted at the end of a P25 transmit
        /// (WPF wrapper loop parity, FneSystemBase.P25.cs:80-82).
        /// </summary>
        private const int P25EndTduCount = 4;

        /// <summary>
        /// Pacing interval of the generated-PCM transmit pump
        /// (<see cref="TransmitPcmAsync"/>): one 320-byte frame per
        /// 20 ms — the codec frame period.
        /// </summary>
        private static readonly TimeSpan TransmitFrameInterval = TimeSpan.FromMilliseconds(20);

        private readonly object _routeGate = new();
        private readonly Dictionary<string, TalkgroupState> _talkgroups =
            new Dictionary<string, TalkgroupState>(StringComparer.Ordinal);

        private readonly object _transmitGate = new();
        private TransmitSession? _session;
        private CaptureAudioPipeline? _capturePipeline;
        private MonitorAudioPipeline? _txMonitorPipeline;
        private DateTime? _lastCaptureRestart;
        private bool _captureRestartInProgress;

        /// <summary>
        /// Optional diagnostic callback supplied by the portable shell. The
        /// Platform project remains independent of Core and keeps its existing
        /// debugger sink when no callback is composed.
        /// </summary>
        public Action<string>? DiagnosticWriter { get; set; }

        /// <summary>
        /// One-shot retry armed when a capture is lost again inside the
        /// throttle window (the replug burst can exhaust the HAL events).
        /// Guarded by <see cref="_transmitGate"/>; nulled by its own
        /// callback and cancelled on deliberate end/dispose.
        /// </summary>
        private IDisposable? _captureRestartRetry;

        private int _disposed;

        /// <summary>
        /// Raised once per capture incarnation (and reset after a successful
        /// device-loss replacement) when the capture stream ends
        /// for any reason other than a deliberate
        /// <see cref="EndTransmitAsync"/> or a stream that ended before it
        /// ever delivered a block: a DeviceLost end verbatim, a Requested
        /// end that was never requested after the stream ran (spurious
        /// stop) surfaced as device loss, and any Error/Cancelled end
        /// verbatim. An end observed after its session is no longer the
        /// current transmit (a quick re-begin, or teardown) stays silent.
        /// The shell marshals this event to its UI thread.
        /// </summary>
        public event Action<AudioStreamEnd>? CaptureEnded;

        /// <summary>
        /// Raised once per monitor pipeline whose playback device was
        /// lost (per-talkgroup receive pipelines and the transmit
        /// loopback monitor). The shell marshals this event to its UI
        /// thread.
        /// </summary>
        public event Action? MonitorStreamEnded;

        /// <summary>
        /// Raised when a receive monitor cannot open its output device. The
        /// decoded observer still receives PCM, so this event is diagnostic and
        /// does not terminate receive routing. The shell marshals it to its UI
        /// thread.
        /// </summary>
        public event Action<string, AudioDeviceException>? MonitorUnavailable;

        /// <summary>
        /// Raised when the receive monitor's idle release ends a talkgroup
        /// stream without an explicit terminator. The shell uses this to
        /// close the matching TAR session exactly once.
        /// </summary>
        public event Action<string, VoiceMode>? TalkgroupStreamEnded;

        /// <summary>
        /// Creates the talkgroup audio router over the given stream
        /// factory and seams.
        /// </summary>
        /// <param name="streams">Factory the router owns and disposes exactly once.</param>
        /// <param name="decoder">Per-codeword voice-frame decoder seam.</param>
        /// <param name="encoder">Per-codeword voice-frame encoder seam.</param>
        /// <param name="sender">Traffic sender receiving complete DMR frames and P25 LDUs.</param>
        /// <param name="resolveOutputDevice">Resolves the output device for monitor playback.</param>
        /// <param name="decodedPcmObserver">
        /// Optional receive observer invoked once per successful decoded
        /// codeword, independently of monitor output selection.
        /// </param>
        /// <param name="transmittedPcmObserver">
        /// Optional observer invoked for each PCM frame selected for every
        /// resolved transmit target before encoding/network delivery.
        /// </param>
        /// <param name="resolveMonitorEnabled">
        /// Optional per-resource local-monitor selection resolver. A null
        /// resolver preserves the legacy behavior and enables monitor
        /// playback for every routed resource.
        /// </param>
        /// <param name="resolveSpeakerOutputEnabled">
        /// Optional per-resource speaker-write resolver. A null resolver
        /// allows speaker writes whenever a monitor pipeline exists. A false
        /// result suppresses only the speaker write; decoded observers and
        /// transmit/network paths remain untouched.
        /// </param>
        /// <param name="resolveTalkgroupOutputDevice">
        /// Optional per-resource output resolver. A null resolver falls back
        /// to <paramref name="resolveOutputDevice"/>.
        /// </param>
        /// <param name="resolveTalkgroupVolume">
        /// Optional per-resource monitor volume resolver in the WPF-compatible
        /// 0..4 range. A null resolver uses unity gain.
        /// </param>
        /// <param name="idleReleaseDelay">
        /// Idle delay before a silent talkgroup's monitor pipeline is
        /// released; defaults to 2 s (WPF AudioManager parity).
        /// </param>
        /// <param name="scheduler">
        /// One-shot scheduler for the idle release; defaults to a real
        /// one-shot <see cref="Timer"/>-based scheduler.
        /// </param>
        /// <param name="clock">UTC clock used by the capture-restart throttle.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="streams"/>, <paramref name="decoder"/>,
        /// <paramref name="encoder"/>, <paramref name="sender"/> or
        /// <paramref name="resolveOutputDevice"/> is null.
        /// </exception>
        public TalkgroupAudioRouter(
            IAudioStreamFactory streams,
            IVoiceFrameDecoder decoder,
            IVoiceFrameEncoder encoder,
            IVoiceTrafficSender sender,
            Func<AudioDeviceId> resolveOutputDevice,
            TimeSpan? idleReleaseDelay = null,
            Func<TimeSpan, Action, IDisposable>? scheduler = null,
            Func<DateTime>? clock = null,
            IDecodedPcmObserver? decodedPcmObserver = null,
            ITransmittedPcmObserver? transmittedPcmObserver = null,
            Func<string, bool>? resolveMonitorEnabled = null,
            Func<string, AudioDeviceId>? resolveTalkgroupOutputDevice = null,
            Func<string, float>? resolveTalkgroupVolume = null,
            Func<string, bool>? resolveSpeakerOutputEnabled = null)
        {
            _factory = streams ?? throw new ArgumentNullException(nameof(streams));
            _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
            _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
            _sender = sender ?? throw new ArgumentNullException(nameof(sender));
            _resolveOutputDevice = resolveOutputDevice
                ?? throw new ArgumentNullException(nameof(resolveOutputDevice));
            _resolveMonitorEnabled = resolveMonitorEnabled ?? (_ => true);
            _resolveSpeakerOutputEnabled = resolveSpeakerOutputEnabled ?? (_ => true);
            _resolveTalkgroupOutputDevice = resolveTalkgroupOutputDevice
                ?? (_ => _resolveOutputDevice());
            _resolveTalkgroupVolume = resolveTalkgroupVolume ?? (_ => 1.0f);
            _decodedPcmObserver = decodedPcmObserver;
            _transmittedPcmObserver = transmittedPcmObserver;
            _idleReleaseDelay = idleReleaseDelay ?? DefaultIdleReleaseDelay;
            _scheduler = scheduler ?? DefaultSchedule;
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        /// <summary>
        /// Stops local monitor playback for one resource without ending its
        /// decoded-observer/TAR lifecycle. A later frame recreates playback
        /// only when the monitor-enable resolver returns true.
        /// </summary>
        public void StopMonitor(string talkgroupKey)
        {
            if (talkgroupKey is null)
            {
                throw new ArgumentNullException(nameof(talkgroupKey));
            }

            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            lock (_routeGate)
            {
                foreach (var entry in _talkgroups)
                {
                    if (!MatchesTalkgroupControlKey(entry.Key, talkgroupKey))
                    {
                        continue;
                    }

                    if (entry.Value.Pipeline is { } pipeline)
                    {
                        StopMonitorLocked(entry.Value, pipeline);
                    }
                    else
                    {
                        entry.Value.ReleaseHandle?.Dispose();
                        entry.Value.ReleaseHandle = null;
                    }
                }
            }
        }

        /// <summary>
        /// Clears every live receive monitor's speaker backlog without
        /// ending decoded observation, TAR, or call-history lifecycles.
        /// Used when RX playback is muted at transmit start.
        /// </summary>
        public void ClearAllTalkgroupBuffers()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            lock (_routeGate)
            {
                foreach (var state in _talkgroups.Values)
                {
                    state.Pipeline?.ClearBuffer();
                }
            }
        }

        /// <summary>
        /// Updates the local monitor volume for a live resource pipeline.
        /// Values are clamped by <see cref="MonitorAudioPipeline.Volume"/>
        /// to the WPF-compatible 0..4 range. A resource without a live
        /// pipeline retains the value through the resolver on its next start.
        /// </summary>
        public void SetMonitorVolume(string talkgroupKey, float volume)
        {
            if (talkgroupKey is null)
            {
                throw new ArgumentNullException(nameof(talkgroupKey));
            }

            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            lock (_routeGate)
            {
                foreach (var entry in _talkgroups)
                {
                    if (MatchesTalkgroupControlKey(entry.Key, talkgroupKey)
                        && entry.Value.Pipeline is { } pipeline)
                    {
                        pipeline.Volume = volume;
                    }
                }
            }
        }

        private static bool MatchesTalkgroupControlKey(string stateKey, string controlKey)
        {
            if (string.Equals(stateKey, controlKey, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return !controlKey.Contains("|slot:", StringComparison.OrdinalIgnoreCase)
                && stateKey.StartsWith(
                    controlKey + "|slot:",
                    StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Routes one received voice frame for the given talkgroup to its
        /// monitor pipeline, creating the pipeline lazily on the first
        /// frame and (re)arming the idle release so new audio keeps the
        /// pipeline alive. DMR frames are split into three 9-byte
        /// codewords and P25 LDUs into nine 11-byte codewords; each
        /// codeword is decoded and written individually (per-codeword
        /// decode granularity, WPF parity). A codeword the decoder
        /// rejects is silently skipped. After the idle release elapses, a
        /// subsequent frame creates a FRESH pipeline.
        /// </summary>
        /// <param name="talkgroupKey">Talkgroup routing key, e.g. "SYS1/TG1".</param>
        /// <param name="frame">The complete voice frame: 27 bytes for DMR, 225 bytes for P25.</param>
        /// <param name="mode">Voice mode of the frame.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="talkgroupKey"/> is null.</exception>
        public void RouteVoiceFrame(string talkgroupKey, ReadOnlyMemory<byte> frame, VoiceMode mode)
        {
            if (talkgroupKey is null)
            {
                throw new ArgumentNullException(nameof(talkgroupKey));
            }

            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            lock (_routeGate)
            {
                // Re-check under the gate: a frame that passed the outer
                // check may still race DisposeAsync, which tears down every
                // pipeline and disposes the factory — creating a pipeline
                // here would throw from the disposed factory into the
                // caller. Skip silently: the router is torn down.
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                if (!_talkgroups.TryGetValue(talkgroupKey, out var state))
                {
                    state = new TalkgroupState();
                    _talkgroups.Add(talkgroupKey, state);
                }

                state.Mode = mode;
                var monitorEnabled = _resolveMonitorEnabled(talkgroupKey);

                if (!monitorEnabled && state.Pipeline is { } disabledPipeline)
                {
                    StopMonitorLocked(state, disabledPipeline);
                }

                if (monitorEnabled && state.Pipeline is null)
                {
                    var pipeline = new MonitorAudioPipeline(_factory);
                    try
                    {
                        pipeline.Start(_resolveTalkgroupOutputDevice(talkgroupKey));
                        pipeline.Volume = _resolveTalkgroupVolume(talkgroupKey);
                    }
                    catch (AudioDeviceException exception)
                    {
                        // Monitor playback is optional. Decoding and the
                        // observer boundary below must still run when the
                        // selected output is unavailable; retry monitor
                        // creation on the next received frame, but notify the
                        // shell only once until a pipeline opens again.
                        if (!state.MonitorUnavailableNotified)
                        {
                            state.MonitorUnavailableNotified = true;
                            WriteDiagnostic(
                                $"Talkgroup monitor unavailable for {talkgroupKey}; observing without playback: {exception.Message}");
                            RaiseMonitorUnavailable(talkgroupKey, exception);
                        }
                    }

                    if (pipeline.IsRunning)
                    {
                        var created = pipeline;
                        created.StreamEnded += _ => OnPipelineStreamEnded(state, created);
                        state.Pipeline = created;
                        state.MonitorUnavailableNotified = false;
                    }
                }

                // Capture the instance for this frame. A DeviceLost write
                // raises the end event synchronously and clears state.Pipeline;
                // the remaining codewords still belong to that stopped
                // instance and must not dereference the cleared state slot.
                var pipelineForFrame = state.Pipeline;

                // New audio resets the idle-release timer only when local
                // monitor playback exists. Observation is independent of
                // this selection and continues when the pipeline is absent.
                if (pipelineForFrame is not null)
                {
                    state.ReleaseHandle?.Dispose();
                    ScheduleRelease(talkgroupKey, state, pipelineForFrame);
                }
                else
                {
                    state.ReleaseHandle?.Dispose();
                    ScheduleObservationRelease(talkgroupKey, state);
                }

                var codewords = mode == VoiceMode.Dmr
                    ? VoiceFrameSplitter.SplitDmrFrame(frame)
                    : VoiceFrameSplitter.SplitP25Ldu(frame);

                foreach (var codeword in codewords)
                {
                    if (!_decoder.TryDecode(codeword, out var samples))
                    {
                        continue;
                    }

                    var pcm = VoiceFrameSplitter.SamplesToBytes(samples);
                    if (pcm.Length != AudioPcm.FrameBytes)
                    {
                        continue;
                    }

                    try
                    {
                        _decodedPcmObserver?.ObserveDecodedPcm(talkgroupKey, mode, pcm);
                    }
                    catch (Exception exception)
                    {
                        // Observation is additive; a TAR/patch subscriber
                        // must never break receive routing or local audio.
                        WriteDiagnostic(
                            $"Decoded PCM observer failed for {talkgroupKey}: {exception.Message}");
                    }

                    if (_resolveSpeakerOutputEnabled(talkgroupKey)
                        && pipelineForFrame is { } monitorPipeline)
                    {
                        var writeResult = monitorPipeline.WritePcm(pcm);
                        if (writeResult.Status != AudioWriteStatus.Accepted)
                        {
                            WriteDiagnostic(
                                $"Talkgroup monitor write rejected for {talkgroupKey}: "
                                + $"{writeResult.Status}, buffered={writeResult.BufferedBytes}");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Begins a transmit for the given target: creates ONE capture
        /// pipeline over the factory, starts the capture on the requested
        /// input device, and routes the raw PCM to a local-monitor output
        /// (250 ms shed) while the capture pump encodes each 320-byte
        /// chunk per-codeword and accumulates complete DMR triples /
        /// P25 LDUs for the traffic sender. The local monitor degrades to
        /// absent when its output device is unavailable; the transmit
        /// itself is unaffected. When the capture stream cannot start
        /// (the typed <see cref="AudioDeviceException"/> when the input
        /// device is unavailable), the loopback monitor created for the
        /// attempt is stopped, no transmit state is committed and the
        /// original exception propagates to the caller. Delegates to the
        /// list overload with a single-element list.
        /// </summary>
        /// <param name="target">The transmit target.</param>
        /// <param name="inputDeviceId">Device to capture microphone audio from.</param>
        /// <param name="cancellationToken">Cancels the capture stream.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when a transmit is already active (single-transmit
        /// gate); end the active transmit first.
        /// </exception>
        /// <exception cref="AudioDeviceException">
        /// Thrown, typed, when the input device is unavailable.
        /// </exception>
        public Task BeginTransmitAsync(
            TransmitTarget target,
            AudioDeviceId inputDeviceId,
            CancellationToken cancellationToken)
        {
            return BeginTransmitAsync(
                new[] { target },
                inputDeviceId,
                cancellationToken);
        }

        /// <summary>
        /// Begins a transmit for the given targets: creates ONE capture
        /// pipeline over the factory, starts the capture on the requested
        /// input device, and routes the raw PCM to a local-monitor output
        /// (250 ms shed) while the capture pump encodes each 320-byte
        /// chunk once per unique voice mode and accumulates complete DMR
        /// triples / P25 LDUs for EVERY target entry of that mode — the
        /// AllChannels PTT fan-out. Each target's frames carry
        /// independent stream-id and sequence counters (per-target state
        /// entries, ordered as supplied). An empty target list is a
        /// completed no-op: no capture, no loopback monitor, no session
        /// state. The local monitor degrades to absent when its output
        /// device is unavailable; the transmit itself is unaffected. When
        /// capture stream cannot start (the typed
        /// <see cref="AudioDeviceException"/> when the input device is
        /// unavailable), the loopback monitor created for the attempt is
        /// stopped, no transmit state is committed and the original
        /// exception propagates to the caller. A P25 target receives one
        /// grant-demand TDU with stream id 0 after capture starts and before
        /// any captured voice; DMR has no call-start signal.
        /// </summary>
        /// <param name="targets">The transmit targets, in fan-out order.</param>
        /// <param name="inputDeviceId">Device to capture microphone audio from.</param>
        /// <param name="cancellationToken">Cancels the capture stream.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="targets"/> is null.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when a transmit is already active (single-transmit
        /// gate); end the active transmit first.
        /// </exception>
        /// <exception cref="AudioDeviceException">
        /// Thrown, typed, when the input device is unavailable.
        /// </exception>
        public async Task BeginTransmitAsync(
            IReadOnlyList<TransmitTarget> targets,
            AudioDeviceId inputDeviceId,
            CancellationToken cancellationToken)
        {
            if (targets is null)
            {
                throw new ArgumentNullException(nameof(targets));
            }

            // An empty target list is a completed no-op: no capture, no
            // loopback monitor, no session state, regardless of lifecycle.
            if (targets.Count == 0)
            {
                return;
            }

            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(TalkgroupAudioRouter));
            }

            // The loopback monitor is created and started before the
            // capture stream and stays untracked (never assigned to
            // _txMonitorPipeline) until the capture start has succeeded,
            // so a failed start can stop it without committing any
            // transmit state.
            MonitorAudioPipeline? monitor = null;

            try
            {
                lock (_transmitGate)
                {
                    if (_session is not null)
                    {
                        throw new InvalidOperationException(
                            "A transmit is already active; call EndTransmitAsync before beginning another.");
                    }

                    // One ordered per-target state entry per supplied
                    // target; a session with no resolvable targets never
                    // reaches this point (the empty list short-circuits
                    // above).
                    var entries = new List<TargetEntry>(targets.Count);
                    foreach (var target in targets)
                    {
                        entries.Add(new TargetEntry(
                            target,
                            target.Mode == VoiceMode.Dmr ? 3 : 9));
                    }

                    var session = new TransmitSession(inputDeviceId, entries);
                    var capture = new CaptureAudioPipeline(_factory);

                    // Local-monitor loopback (WPF AddLiveMonitorStream parity,
                    // MainWindow.xaml.cs:3196-3197): the captured PCM is routed
                    // to the resolved output device through a monitor pipeline
                    // with the 250 ms shed. An unavailable monitor device
                    // degrades to no loopback rather than failing the transmit.
                    try
                    {
                        monitor = new MonitorAudioPipeline(_factory);
                        monitor.Start(_resolveOutputDevice());
                        monitor.StreamEnded += OnTxMonitorStreamEnded;
                    }
                    catch (AudioDeviceException exception)
                    {
                        WriteDiagnostic(
                            $"PTT local monitor unavailable; transmitting without loopback: {exception.Message}");
                        monitor = null;
                    }

                    var endTask = capture.StartAsync(
                        inputDeviceId,
                        block => PumpBlockAsync(session, block),
                        cancellationToken);

                    _session = session;
                    _capturePipeline = capture;
                    _txMonitorPipeline = monitor;

                    // WPF PTT parity: a P25 press starts the call with one
                    // grant-demand TDU before any voice LDU. Keep stream id
                    // zero for this control packet; the first voice LDU
                    // receives the session's first live stream id from the
                    // per-target accumulator.
                    foreach (var entry in session.Targets)
                    {
                        if (entry.Target.Mode == VoiceMode.P25)
                        {
                            _sender.SendP25Tdu(entry.Target, streamId: 0, grantDemand: true);
                        }
                    }

                    ObserveEndAsync(endTask, session);
                }
            }
            catch
            {
                // The capture stream failed to start (the typed
                // AudioDeviceException when the microphone is unavailable —
                // the common macOS permission case). The loopback monitor is
                // live but untracked: stop it here so no output device stays
                // held, no subscription outlives the attempt and the factory
                // is never disposed underneath a live pipeline. Transmit
                // state is already fully reset — nothing was committed to
                // _session/_capturePipeline/_txMonitorPipeline and the
                // per-session end flags die with the discarded session. The
                // original exception propagates to the caller.
                if (monitor is not null)
                {
                    try
                    {
                        await monitor.StopAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        // A failing stop must never mask the original start
                        // failure.
                    }
                }

                throw;
            }
        }

        /// <summary>
        /// Retries the capture for the current transmit after a
        /// <see cref="AudioStreamStopReason.DeviceLost"/> end. The session,
        /// target, input device, pending codewords, stream id and sequence
        /// number are retained so a HAL unplug does not split the logical
        /// transmission. A successful retry is throttled for ten seconds;
        /// a failed retry remains pending for the next
        /// <c>MacAudioDeviceCatalog.DevicesChanged</c> notification.
        /// </summary>
        /// <remarks>
        /// WPF force-restarts its input immediately from RecordingStopped.
        /// macOS deliberately waits for the catalog's device-change signal
        /// after a failed retry, avoiding a restart storm while the device is
        /// still absent.
        /// </remarks>
        public async Task<bool> RequestCaptureRestartAsync()
        {
            TransmitSession? session;
            CaptureAudioPipeline? oldCapture;
            var success = false;

            lock (_transmitGate)
            {
                if (Volatile.Read(ref _disposed) != 0
                    || _session is not { } active
                    || !active.RestartPending
                    || _captureRestartInProgress)
                {
                    return false;
                }

                var now = _clock();
                if (_lastCaptureRestart is { } last
                    && now - last < CaptureRestartThrottle)
                {
                    // A fast replug burst (device-list + default-device
                    // changes) can all land inside the window after a
                    // successful restart. If the capture is already lost
                    // again, no further HAL event will arrive, so arm a
                    // one-shot retry at the end of the window instead of
                    // stranding the session on a dead capture (WPF
                    // force-restarts from RecordingStopped; macOS waits
                    // for this retry or the next device change). The
                    // callback self-cancels on fire and re-enters this
                    // method; a still-absent device leaves pending set and
                    // does not consume a fresh throttle window.
                    if (active.RestartPending && _captureRestartRetry is null)
                    {
                        var remaining = CaptureRestartThrottle - (now - last);
                        IDisposable? handle = null;
                        handle = _scheduler(remaining, () =>
                        {
                            handle?.Dispose();
                            _captureRestartRetry = null;
                            _ = RequestCaptureRestartAsync();
                        });
                        _captureRestartRetry = handle;
                    }

                    return false;
                }

                _captureRestartInProgress = true;
                session = active;
                oldCapture = _capturePipeline;
                _capturePipeline = null;
            }

            try
            {
                if (oldCapture is not null)
                {
                    await oldCapture.StopAsync().ConfigureAwait(false);
                }

                var replacement = new CaptureAudioPipeline(_factory);
                Task<AudioStreamEnd> endTask;
                try
                {
                    endTask = replacement.StartAsync(
                        session.InputDeviceId,
                        block => PumpBlockAsync(session, block),
                        CancellationToken.None);
                }
                catch
                {
                    try
                    {
                        await replacement.StopAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        // Preserve the device-start failure.
                    }

                    throw;
                }

                var keepReplacement = false;
                lock (_transmitGate)
                {
                    if (Volatile.Read(ref _disposed) == 0
                        && ReferenceEquals(_session, session)
                        && !session.TransmitEnded)
                    {
                        _capturePipeline = replacement;
                        session.RestartPending = false;
                        Interlocked.Exchange(ref session.CaptureEndedRaised, 0);
                        _lastCaptureRestart = _clock();
                        keepReplacement = true;
                        success = true;
                    }
                }

                if (!keepReplacement)
                {
                    await replacement.StopAsync().ConfigureAwait(false);
                    return false;
                }

                ObserveEndAsync(endTask, session);
                return true;
            }
            catch (Exception exception)
            {
                WriteDiagnostic(
                    $"PTT capture restart unavailable; waiting for the next device change: {exception}");
                return false;
            }
            finally
            {
                lock (_transmitGate)
                {
                    _captureRestartInProgress = false;
                    if (!success
                        && ReferenceEquals(_session, session)
                        && !session.TransmitEnded)
                    {
                        session.RestartPending = true;
                    }
                }
            }
        }

        /// <summary>
        /// Ends the active transmit: stops the capture input (no further
        /// sends; a late pump block after the end is a no-op) and the
        /// local-monitor loopback, then emits the end-of-call signalling
        /// once per active target — a DMR terminator, or four P25 TDUs
        /// (WPF parity) — carrying the target's own stream id and next
        /// sequence number. Idempotent when no transmit is active (a
        /// second call emits nothing; the session-scoped emission runs
        /// exactly once per transmit, after the streams are stopped).
        /// </summary>
        public async Task EndTransmitAsync()
        {
            CaptureAudioPipeline? capture;
            MonitorAudioPipeline? monitor;
            IReadOnlyList<TargetEntry>? targets = null;

            lock (_transmitGate)
            {
                if (_session is null)
                {
                    return;
                }

                var active = _session;

                // Mark the end as deliberate BEFORE stopping so the
                // end-task observer never surfaces it as an unexpected
                // capture end (per-session: a quick re-begin cannot reset
                // this session's end state).
                active.EndRequested = true;
                active.TransmitEnded = true;

                // The per-target entries live with the session: their
                // stream/seq counters are the final values of this
                // transmit (the pump is stopped before they are read).
                targets = active.Targets;

                capture = _capturePipeline;
                monitor = _txMonitorPipeline;
                _capturePipeline = null;
                _txMonitorPipeline = null;
                _session = null;

                // A deliberate end cancels any armed capture-restart retry.
                _captureRestartRetry?.Dispose();
                _captureRestartRetry = null;
            }

            var stops = new List<Task>(2);
            if (capture is not null)
            {
                stops.Add(capture.StopAsync());
            }

            if (monitor is not null)
            {
                stops.Add(monitor.StopAsync());
            }

            await Task.WhenAll(stops).ConfigureAwait(false);

            // End-of-call signalling, once per active target, AFTER the
            // capture and monitor are stopped: a DMR transmit ends with
            // one terminator, a P25 transmit with four TDUs (WPF
            // wrapper loop parity, FneSystemBase.P25.cs:80-82). Each
            // signal carries the target's own stream id and next
            // sequence number (WPF sends stream id 0 and hardcodes the
            // DMR slot — both port deltas). The sender contract is
            // never-throw, so the fire-and-forget caller cannot fault
            // here.
            if (targets is not null)
            {
                foreach (var entry in targets)
                {
                    if (entry.Target.Mode == VoiceMode.Dmr)
                    {
                        _sender.SendDmrTerminator(entry.Target, entry.StreamIdCounter, entry.SeqNo);
                    }
                    else
                    {
                        for (var i = 0; i < P25EndTduCount; i++)
                        {
                            _sender.SendP25Tdu(entry.Target, entry.StreamIdCounter, grantDemand: false);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Plays a short local PCM buffer through the resolved speaker output.
        /// This method never opens capture, sends traffic, or observes TAR
        /// audio. The router stops and disposes only the temporary output;
        /// the router-owned stream factory remains alive. Device failures
        /// are best-effort and do not break PTT.
        /// </summary>
        public async Task PlayLocalPcmAsync(ReadOnlyMemory<byte> pcm)
        {
            if (pcm.Length == 0 || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            IAudioOutput? output = null;
            try
            {
                output = _factory.CreateOutput(_resolveOutputDevice(), AudioPcm.Console);
                var result = output.Write(pcm);
                if (result.Status == AudioWriteStatus.Accepted)
                {
                    var duration = TimeSpan.FromSeconds(
                        (double)pcm.Length / AudioPcm.Console.BytesPerSecond);
                    await Task.Delay(duration).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                WriteDiagnostic(
                    $"Local PCM playback unavailable: {exception.Message}");
            }
            finally
            {
                if (output is not null)
                {
                    try
                    {
                        await output.StopAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        WriteDiagnostic(
                            $"Local PCM output stop failed: {exception.Message}");
                    }

                    if (output is IAsyncDisposable disposable)
                    {
                        try
                        {
                            await disposable.DisposeAsync().ConfigureAwait(false);
                        }
                        catch (Exception exception)
                        {
                            WriteDiagnostic(
                                $"Local PCM output dispose failed: {exception.Message}");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Transmits one-shot, generated PCM as a complete additive
        /// transmit. Core supplies the fully synthesized and normalized
        /// PCM (8000 Hz / 16-bit / mono alert-tone audio), and this
        /// method owns the pacing, encoding, accumulation and sender
        /// delivery — no capture input is opened, no local-monitor
        /// loopback is created and file playback is never used.
        /// Playback of the generated PCM is a separate shell seam: the
        /// shell renders any local audio through its own playback path
        /// while the router only transports the traffic. Each 320-byte
        /// frame is encoded once per unique voice mode through the
        /// injected encoder and fanned out to every target entry of
        /// that mode (the AllChannels fan-out, with independent
        /// per-target stream/seq counters), paced at one frame per
        /// 20 ms with a cancellable delay. When
        /// <paramref name="sendStartSignal"/> is set, one P25
        /// grant-demand TDU (initial stream id 0) is sent per P25
        /// target before the PCM; DMR has no start signal. The
        /// transmit ends exactly once — even on cancellation — with
        /// the same end-of-call signalling as
        /// <see cref="EndTransmitAsync"/>: one DMR terminator per DMR
        /// target, or four non-grant P25 TDUs per P25 target, using
        /// the entries' own stream/sequence counters. If the shell
        /// already ended the session via <see cref="EndTransmitAsync"/>,
        /// that end stands and the signals are not duplicated. The
        /// generated session is serialized against the capture
        /// transmit through the same single-transmit gate
        /// (<see cref="_session"/>).
        /// </summary>
        /// <param name="targets">The transmit targets, in fan-out order.</param>
        /// <param name="pcm">
        /// Synthesized/normalized PCM from Core, an exact whole number
        /// of <see cref="AudioPcm.FrameBytes"/> frames.
        /// </param>
        /// <param name="sendStartSignal">
        /// True to send one P25 grant-demand TDU per P25 target
        /// (stream id 0) before the PCM; no DMR start signal exists.
        /// </param>
        /// <param name="cancellationToken">Cancels the paced frame loop.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="targets"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="pcm"/> length is not a multiple
        /// of <see cref="AudioPcm.FrameBytes"/>.
        /// </exception>
        /// <exception cref="ObjectDisposedException">
        /// Thrown when the router is disposed and the call is not an
        /// empty no-op.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when a transmit is already active (single-transmit
        /// gate); end the active transmit first.
        /// </exception>
        public async Task TransmitPcmAsync(
            IReadOnlyList<TransmitTarget> targets,
            ReadOnlyMemory<byte> pcm,
            bool sendStartSignal,
            CancellationToken cancellationToken)
        {
            if (targets is null)
            {
                throw new ArgumentNullException(nameof(targets));
            }

            // An empty target list or empty PCM is a completed no-op,
            // regardless of lifecycle: no session, no signalling, no
            // sender traffic.
            if (targets.Count == 0 || pcm.Length == 0)
            {
                return;
            }

            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(TalkgroupAudioRouter));
            }

            if (pcm.Length % AudioPcm.FrameBytes != 0)
            {
                throw new ArgumentException(
                    $"Generated PCM length must be a multiple of {AudioPcm.FrameBytes} bytes (one 20 ms frame).",
                    nameof(pcm));
            }

            TransmitSession session;
            lock (_transmitGate)
            {
                if (_session is not null)
                {
                    throw new InvalidOperationException(
                        "A transmit is already active; call EndTransmitAsync before beginning another.");
                }

                // One ordered per-target state entry per supplied
                // target, built exactly as BeginTransmitAsync builds
                // them (3 codewords per DMR unit, 9 per P25 LDU). The
                // generated session uses the default device — the id is
                // never used to open a capture input.
                var entries = new List<TargetEntry>(targets.Count);
                foreach (var target in targets)
                {
                    entries.Add(new TargetEntry(
                        target,
                        target.Mode == VoiceMode.Dmr ? 3 : 9));
                }

                session = new TransmitSession(AudioDeviceId.Default, entries);
                _session = session;
            }

            try
            {
                // P25 call-start signalling, once per P25 target,
                // BEFORE the PCM, on the initial stream id 0; DMR has
                // no start signal (a grant-demand TDU is P25-only).
                if (sendStartSignal)
                {
                    foreach (var entry in session.Targets)
                    {
                        if (entry.Target.Mode == VoiceMode.P25)
                        {
                            _sender.SendP25Tdu(entry.Target, streamId: 0, grantDemand: true);
                        }
                    }
                }

                var frameCount = pcm.Length / AudioPcm.FrameBytes;
                for (var i = 0; i < frameCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // A concurrent EndTransmitAsync (shell) ended the
                    // session: stop pumping so no voice frames follow
                    // its terminators; the finally block then skips the
                    // duplicate end signalling because the session is no
                    // longer ours.
                    if (session.TransmitEnded)
                    {
                        break;
                    }

                    // Pace one frame per 20 ms; the delay is cancellable.
                    if (i > 0)
                    {
                        await Task.Delay(TransmitFrameInterval, cancellationToken).ConfigureAwait(false);
                    }

                    await PumpPcmFrame(
                        session,
                        pcm.Slice(i * AudioPcm.FrameBytes, AudioPcm.FrameBytes)).ConfigureAwait(false);
                }
            }
            finally
            {
                // End exactly once: clear the generated session only if
                // it is still the router's current transmit, and only
                // then emit the end-of-call signalling — one DMR
                // terminator per DMR target, four non-grant P25 TDUs
                // per P25 target — using the entries' own stream/seq
                // counters (parity EndTransmitAsync). If
                // EndTransmitAsync (or dispose) already ended the
                // session, _session is no longer ours and nothing is
                // duplicated; the end signals are delivered even when
                // the frame loop exited on cancellation.
                var ownsSession = false;
                lock (_transmitGate)
                {
                    if (ReferenceEquals(_session, session))
                    {
                        _session = null;
                        ownsSession = true;
                    }
                }

                if (ownsSession)
                {
                    foreach (var entry in session.Targets)
                    {
                        if (entry.Target.Mode == VoiceMode.Dmr)
                        {
                            _sender.SendDmrTerminator(entry.Target, entry.StreamIdCounter, entry.SeqNo);
                        }
                        else
                        {
                            for (var i = 0; i < P25EndTduCount; i++)
                            {
                                _sender.SendP25Tdu(entry.Target, entry.StreamIdCounter, grantDemand: false);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Stops every live pipeline (per-talkgroup monitors, the capture
        /// input and the transmit loopback monitor), cancels all pending
        /// idle releases and disposes the owned factory exactly once.
        /// Idempotent; no events are raised during or after teardown.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            // The active session's end is marked deliberate inside the
            // transmit gate below; the capture-end observer and monitor
            // events stay silent during teardown.

            var stops = new List<Task>();

            lock (_routeGate)
            {
                foreach (var state in _talkgroups.Values)
                {
                    state.ReleaseHandle?.Dispose();
                    state.ReleaseHandle = null;
                    if (state.Pipeline is { } pipeline)
                    {
                        state.Pipeline = null;
                        stops.Add(pipeline.StopAsync());
                    }
                }

                _talkgroups.Clear();
            }

            lock (_transmitGate)
            {
                // Mark the current session's end as deliberate so its
                // end-task observer stays silent during teardown (the
                // observer additionally ignores any end once the session
                // is no longer current).
                if (_session is { } active)
                {
                    active.EndRequested = true;
                    active.TransmitEnded = true;
                }

                if (_txMonitorPipeline is { } monitor)
                {
                    _txMonitorPipeline = null;
                    stops.Add(monitor.StopAsync());
                }

                if (_capturePipeline is { } capture)
                {
                    _capturePipeline = null;
                    stops.Add(capture.StopAsync());
                }

                // Teardown cancels any armed capture-restart retry; the
                // disposed guard already makes a late fire a no-op.
                _captureRestartRetry?.Dispose();
                _captureRestartRetry = null;

                _session = null;
            }

            await Task.WhenAll(stops).ConfigureAwait(false);

            // The factory is owned by the router and disposed exactly
            // once; the pipelines above are only ever STOPPED (their
            // DisposeAsync would dispose the shared factory).
            await _factory.DisposeAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Arms the idle release for one talkgroup pipeline: after
        /// <see cref="_idleReleaseDelay"/> the pipeline is stopped and
        /// marked released so the next frame creates a fresh one. The
        /// action self-cancels when it fires, so a scheduler that
        /// re-invokes the action (or a dispose racing the fire) is a
        /// no-op the second time.
        /// </summary>
        private void ScheduleRelease(string talkgroupKey, TalkgroupState state, MonitorAudioPipeline pipeline)
        {
            IDisposable? handle = null;
            handle = _scheduler(_idleReleaseDelay, () =>
            {
                handle?.Dispose();
                ReleaseTalkgroup(talkgroupKey, state, pipeline);
            });
            state.ReleaseHandle = handle;
        }

        /// <summary>
        /// Arms the same idle lifecycle when monitor output is unavailable.
        /// Decoded observation must not depend on a playback pipeline, but
        /// the recording session still needs an idle terminator.
        /// </summary>
        private void ScheduleObservationRelease(string talkgroupKey, TalkgroupState state)
        {
            IDisposable? handle = null;
            handle = _scheduler(_idleReleaseDelay, () =>
            {
                handle?.Dispose();
                ReleaseObservationOnly(talkgroupKey, state);
            });
            state.ReleaseHandle = handle;
        }

        private static void StopMonitorLocked(TalkgroupState state, MonitorAudioPipeline pipeline)
        {
            state.ReleaseHandle?.Dispose();
            state.ReleaseHandle = null;
            if (ReferenceEquals(state.Pipeline, pipeline))
            {
                state.Pipeline = null;
            }

            // Stopped, never disposed: the pipeline shares the router-owned
            // stream factory with every other receive/transmit pipeline.
            _ = pipeline.StopAsync();
        }

        /// <summary>
        /// Idle release: stops the talkgroup's pipeline and marks it
        /// released so subsequent frames create a fresh pipeline.
        /// </summary>
        private void ReleaseTalkgroup(string talkgroupKey, TalkgroupState state, MonitorAudioPipeline pipeline)
        {
            VoiceMode? mode = null;
            lock (_routeGate)
            {
                if (state.Pipeline == pipeline)
                {
                    state.Pipeline = null;
                    mode = state.Mode;
                }
            }

            // Stopped, never disposed: the pipeline must not dispose the
            // shared factory.
            _ = pipeline.StopAsync();
            if (mode is { } releasedMode && Volatile.Read(ref _disposed) == 0)
            {
                TalkgroupStreamEnded?.Invoke(talkgroupKey, releasedMode);
            }
        }

        private void ReleaseObservationOnly(string talkgroupKey, TalkgroupState state)
        {
            VoiceMode? mode = null;
            lock (_routeGate)
            {
                if (_talkgroups.TryGetValue(talkgroupKey, out var current)
                    && ReferenceEquals(current, state)
                    && state.Pipeline is null)
                {
                    state.ReleaseHandle = null;
                    mode = state.Mode;
                }
            }

            if (mode is { } releasedMode && Volatile.Read(ref _disposed) == 0)
            {
                TalkgroupStreamEnded?.Invoke(talkgroupKey, releasedMode);
            }
        }

        /// <summary>
        /// Forwards a per-talkgroup monitor pipeline's device loss: the
        /// pipeline is marked released (the next frame re-creates it) and
        /// <see cref="MonitorStreamEnded"/> is raised once (the pipeline
        /// itself raises its event exactly once).
        /// </summary>
        private void OnPipelineStreamEnded(TalkgroupState state, MonitorAudioPipeline pipeline)
        {
            lock (_routeGate)
            {
                if (state.Pipeline == pipeline)
                {
                    state.Pipeline = null;
                }
            }

            _ = pipeline.StopAsync();
            MonitorStreamEnded?.Invoke();
        }

        /// <summary>
        /// Surfaces the transmit loopback monitor's device loss through
        /// <see cref="MonitorStreamEnded"/> (once; the pipeline raises its
        /// event exactly once). The transmit itself keeps running — writes
        /// into the lost pipeline report DeviceLost harmlessly.
        /// </summary>
        private void OnTxMonitorStreamEnded(AudioStreamEnd end) => MonitorStreamEnded?.Invoke();

        /// <summary>
        /// Encodes one 320-byte PCM frame ONCE per unique voice mode
        /// present in the session and accumulates the encoded codeword
        /// into every target entry of that mode, sending each entry's
        /// complete DMR triples / P25 LDUs with independent stream/seq
        /// counters (the AllChannels fan-out). A frame the encoder
        /// rejects is skipped. Shared by the capture pump
        /// (<see cref="PumpBlockAsync"/>, after the loopback-monitor
        /// write) and the generated-PCM pump
        /// (<see cref="TransmitPcmAsync"/>, which never writes a
        /// monitor).
        /// </summary>
        private Task PumpPcmFrame(TransmitSession session, ReadOnlyMemory<byte> frame)
        {
            var samples = VoiceFrameSplitter.BytesToSamples(frame);

            // Encode ONCE per unique mode (all targets of the same
            // mode share the same encoded codeword; the encoder seam
            // is mode-keyed, so a second encode of the same frame for
            // a sibling target would be redundant), then accumulate
            // the codeword into every entry of that mode.
            var encodedModes = new HashSet<VoiceMode>();
            foreach (var entry in session.Targets)
            {
                try
                {
                    _transmittedPcmObserver?.ObserveTransmittedPcm(entry.Target, frame);
                }
                catch (Exception exception)
                {
                    WriteDiagnostic(
                        $"Transmitted PCM observer failed for {entry.Target.SystemName}: {exception.Message}");
                }

                if (!encodedModes.Add(entry.Target.Mode))
                {
                    continue;
                }

                if (!_encoder.TryEncode(entry.Target.Mode, samples, out var codeword))
                {
                    continue;
                }

                AccumulateAndSend(session, entry.Target.Mode, codeword);
            }

            return Task.CompletedTask;
        }

        private void RaiseMonitorUnavailable(string talkgroupKey, AudioDeviceException exception)
        {
            try
            {
                MonitorUnavailable?.Invoke(talkgroupKey, exception);
            }
            catch (Exception notificationException)
            {
                WriteDiagnostic(
                    $"Monitor-unavailable notification failed for {talkgroupKey}: {notificationException.Message}");
            }
        }

        private void WriteDiagnostic(string message)
        {
            try
            {
                if (DiagnosticWriter is { } writer)
                {
                    writer(message);
                    return;
                }
            }
            catch
            {
                // Diagnostics must never break the audio path.
            }

            System.Diagnostics.Debug.WriteLine(message);
        }

        /// <summary>
        /// Capture pump callback (runs on the capture thread): splits each
        /// 1600-byte block into five 320-byte chunks, routes each chunk to
        /// the local monitor and encodes/accumulates it through
        /// <see cref="PumpPcmFrame"/> (once per unique voice mode, the
        /// AllChannels fan-out, per-target stream/seq counters). A chunk
        /// the encoder rejects is skipped; a block delivered after the
        /// session's <see cref="EndTransmitAsync"/> sends nothing (the
        /// ended flag is per-session, so a late block on a prior
        /// session's pump after a quick re-begin is a no-op).
        /// </summary>
        private Task PumpBlockAsync(TransmitSession session, ReadOnlyMemory<byte> block)
        {
            if (session.TransmitEnded)
            {
                return Task.CompletedTask;
            }

            // The stream delivered a block: the session is live, so a
            // later Requested end is a genuine spurious stop rather than a
            // start artifact.
            Volatile.Write(ref session.PumpedBlocks, 1);

            foreach (var chunk in VoiceFrameSplitter.SplitBlock(block))
            {
                if (session.TransmitEnded)
                {
                    break;
                }

                if (Volatile.Read(ref _txMonitorPipeline) is { } monitor)
                {
                    monitor.WritePcm(chunk);
                }

                PumpPcmFrame(session, chunk);
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Accumulates one encoded codeword into EVERY target entry of
        /// the given mode and sends each entry's complete units: a DMR
        /// AMBE triple (three 9-byte codewords concatenated into a
        /// 27-byte frame, WPF MainWindow.DMR.cs:126-130 parity) via
        /// <see cref="IVoiceTrafficSender.SendDmrVoice"/>, or a P25 LDU
        /// (nine 11-byte IMBE codewords placed at the locked WPF offsets
        /// within a 225-byte LDU, MainWindow.P25.cs:154-178 parity) via
        /// <see cref="IVoiceTrafficSender.SendP25Ldu"/> (LDU1), each with
        /// the entry's own monotonically increasing stream id and
        /// per-frame sequence number, so sibling targets never share
        /// stream/seq state. Partial units carry over to the next block.
        /// </summary>
        private void AccumulateAndSend(TransmitSession session, VoiceMode mode, byte[] codeword)
        {
            foreach (var entry in session.Targets)
            {
                if (entry.Target.Mode != mode)
                {
                    continue;
                }

                entry.PendingCodewords.Add(codeword);

                while (entry.PendingCodewords.Count >= entry.CodewordsPerUnit)
                {
                    var unit = AssembleUnit(
                        entry,
                        entry.PendingCodewords.GetRange(0, entry.CodewordsPerUnit));
                    entry.PendingCodewords.RemoveRange(0, entry.CodewordsPerUnit);

                    var streamId = ++entry.StreamIdCounter;
                    var seqNo = entry.SeqNo++;

                    if (entry.Target.Mode == VoiceMode.Dmr)
                    {
                        _sender.SendDmrVoice(entry.Target, unit, streamId, seqNo);
                    }
                    else
                    {
                        _sender.SendP25Ldu(entry.Target, isLdu2: false, unit, streamId, seqNo);
                    }
                }
            }
        }

        /// <summary>
        /// Assembles one complete transmit unit from its codewords: a DMR
        /// triple is the three 9-byte codewords concatenated (27 bytes); a
        /// P25 LDU is a 225-byte buffer with each 11-byte IMBE codeword
        /// placed at its locked WPF offset (10, 26, 55, 80, 105, 130,
        /// 155, 180, 204) and the remaining LDU fields (NID, parity, ...)
        /// zeroed — the fnecore traffic adapter (follow-on slice) fills
        /// them.
        /// </summary>
        private static byte[] AssembleUnit(TargetEntry entry, List<byte[]> codewords)
        {
            if (entry.Target.Mode == VoiceMode.Dmr)
            {
                var unit = new byte[27];
                for (var i = 0; i < codewords.Count; i++)
                {
                    codewords[i].CopyTo(unit, i * 9);
                }

                return unit;
            }

            var ldu = new byte[225];
            for (var i = 0; i < codewords.Count; i++)
            {
                codewords[i].CopyTo(ldu, VoiceFrameSplitter.P25CodewordOffsets[i]);
            }

            return ldu;
        }

        /// <summary>
        /// Observes the capture end task and raises
        /// <see cref="CaptureEnded"/> exactly once per transmit when the
        /// stream ends for any reason other than a deliberate
        /// <see cref="EndTransmitAsync"/> or a stream that ended before it
        /// ever delivered a block: a DeviceLost/Error/Cancelled end
        /// verbatim; a Requested end that was never requested (spurious
        /// stop) surfaced as device loss. The observation is bound to the
        /// session that owns the end task and only raises while that
        /// session is still the router's current transmit, so a prior
        /// session's end task completing after a quick re-begin stays
        /// silent. The continuation runs inline on the end task's
        /// completing thread (no extra pool hop) and never throws.
        /// </summary>
        private void ObserveEndAsync(Task<AudioStreamEnd> endTask, TransmitSession session)
        {
            endTask.ContinueWith(
                static (task, state) =>
                {
                    var (router, owner) = ((TalkgroupAudioRouter, TransmitSession))state!;
                    router.RaiseCaptureEnded(task, owner);
                },
                (this, session),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void RaiseCaptureEnded(Task<AudioStreamEnd> endTask, TransmitSession session)
        {
            AudioStreamEnd end;
            try
            {
                end = endTask.GetAwaiter().GetResult();
            }
            catch
            {
                end = AudioStreamEnd.Error(
                    AudioDeviceErrorKind.Unknown,
                    "The capture end task failed.");
            }

            // Only the session that owns the end task may raise: a prior
            // session's end task completing after a quick re-begin must
            // not surface as a spurious capture end, and teardown (which
            // nulls _session) stays silent.
            if (!ReferenceEquals(Volatile.Read(ref _session), session))
            {
                return;
            }

            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            if (end.StopReason == AudioStreamStopReason.Requested
                && (session.EndRequested || Volatile.Read(ref session.PumpedBlocks) == 0))
            {
                return;
            }

            if (Interlocked.Exchange(ref session.CaptureEndedRaised, 1) != 0)
            {
                return;
            }

            var raised = end.StopReason == AudioStreamStopReason.Requested
                ? AudioStreamEnd.DeviceLost()
                : end;

            if (raised.StopReason == AudioStreamStopReason.DeviceLost)
            {
                session.RestartPending = true;
            }

            try
            {
                CaptureEnded?.Invoke(raised);
            }
            catch
            {
                // A throwing subscriber must never fault the observation
                // task or the stream that completed it.
            }
        }

        /// <summary>
        /// Default one-shot scheduler: a real
        /// <see cref="System.Threading.Timer"/> that fires the action
        /// once after the delay (parity FneConnectionService.DefaultSchedule).
        /// </summary>
        private static IDisposable DefaultSchedule(TimeSpan delay, Action action)
        {
            var timer = new Timer(
                _ => action(),
                null,
                delay,
                Timeout.InfiniteTimeSpan);

            return new TimerCancellation(timer);
        }
    }
}
