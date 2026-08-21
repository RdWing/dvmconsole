# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.3.2] - 2026-08-20

### Added

- Add a second OS-global keyboard PTT binding that keys only the TX-selected resources in the active system, using the same press-and-hold or toggle setting as global PTT, plus an independent active-system scope option for serial hardware PTT.

### Changed

- Show Event History call durations consistently with tenths of a second, adding minute and hour units for longer calls.
- Replace the single RX audio-processing toggle with per-mode high-pass, peaking-EQ, and soft-knee compressor controls. Keep decoder boundary smoothing fixed on, and raise the fixed DMR, NXDN, and P25 Phase 2 presentation gain from 6 dB to 9 dB while leaving P25 Phase 1 at unity gain.
- Document that the current FNE plaintext and legacy encrypted transports should be used only across a trusted network or an authenticated VPN because they do not mutually authenticate the master.

### Fixed

- Reveal a call's TAR recording directly in Finder or File Explorer when its Activity sidebar row is double-clicked.
- Let Debug Logs search for multiple space-separated terms anywhere in an entry, make Clear Text reset only the entered search, and describe vocoder level windows by elapsed time instead of sample count.
- Close timed-out receive streams before applying later traffic so channel cards, History, and TAR state cannot remain pinned to an ended call when a UI cleanup tick is delayed.
- Keep every RX audio-processing spinner value visible and list the mode rows as P25 Phase 1, P25 Phase 2, DMR, and NXDN.
- Preserve simultaneous calls that share one talkgroup with independent receive lifecycles, decoder state, mixer lanes, and TAR writers, while refilling ready live-audio frames when the output buffer falls behind.
- Keep microphone audio suppressed until a Bluetooth talk-permit cue completes after sustained cold-microphone capture and extended output settling, classify the physical macOS route so known non-Bluetooth devices keep the shorter cue, revalidate and retry transient route changes, and stop PTT instead of transmitting without the requested indication.
- Load and prune TAR catalog entries only from metadata embedded in Opus recordings, without scanning or deleting legacy JSON sidecars.
- Accept FNE/KMM P25 key material only once for the matching algorithm and key ID requested during the current bounded response window, without requiring codeplug changes.
- Reject truncated, length-inconsistent, and parser-unsafe FNE datagrams before they reach the pinned upstream decoder, and discard exact encrypted wire replays within a bounded receive window.
- Disable unused inbound FNE metadata inventory and master talkgroup-announcement inputs so they cannot accumulate unbounded upstream state.
- Restore a selected web stream automatically only when its codeplug path, canonical URL, and credentials match the stream the operator previously started; legacy name-only selections remain off until manually selected again.
- Keep the audible Bluetooth talk-permit tone the same length for cold and warm PTT, reopen a cold AirPods output after microphone readiness so the cue uses the duplex profile, protect the cold cue's audible edge with a short silent lead-in, reduce the post-transition safety margins, and report phase-by-phase timing diagnostics as elapsed time without releasing microphone audio before the cue completes.
- Keep the live RX output clock continuous across delayed FNE packets, reconcile later concealment against audio time already presented as silence, and reduce the startup and normal speaker cushions to 80 ms with a bounded 120 ms recovery target. Normalize macOS CoreAudio queue depth into the requested 8 kHz format so those targets represent real device time instead of native-rate sample counts.
- Retain complete packet-loss concealment in TAR while bounding stale live concealment and live lanes to 320 ms; resynchronize an overflowing lane once toward the current 80 ms window instead of remaining delayed. Report live gap fill, skipped concealment, actual CoreAudio starvation, and the most recent overflowing lane.
- During a cold Bluetooth PTT profile transition, discard only live speaker-bound RX PCM instead of replaying a delayed backlog while preserving call state and TAR observation; report that intentional discard separately, and distinguish RTP sequencing issues, receive-queue drops, and post-call late traffic.
- Timestamp traffic at the app-owned FNE event boundary and route selected receive traffic directly into its ordered per-channel decoder worker so unrelated UI presentation traffic cannot delay live audio while TAR continues to receive the same decoded samples. Report boundary-to-audio queue, worker, and processing high-water timing, and keep physical-output continuity diagnostics active across unexpected mid-call source gaps.
- Drive channel-card receive meters from speaker-bound PCM with the measured physical queue delay so their movement follows audible playback rather than earlier decoder bursts.
- Keep completed receive-stream tombstones out of live playback, gain, and balance changes; serialize card, zone, bulk, transmit-mute, and alert-tone RX transitions; and reconcile selected cards that have lost their live session. This prevents disposed-playback crashes, enabled-but-silent cards, inconsistent persisted RX state, and intermittent tone sends while receive audio is muted or restored.
- Tie system and zone activity lamps to enabled live-RX presentation so raw startup traffic cannot light disabled tabs. Tombstone timed-out decoder and mixer streams before the priority ingress path can accept delayed packets for an ended call.
- Persist the RX/Listen selection of every channel card independently from TAR arming and restore those cards across all tabs when startup selection restoration is enabled. Keep TAR-only decoders active without feeding or lighting the live speaker lane.
- Default the warm transmit microphone to off while continuing to persist the operator's last selection, let History searches combine multiple terms across any displayed or recording field, retain more Debug Log entries, and coalesce repeated RX diagnostic snapshots.
- Hold an explicitly terminated receive stream off-screen for a bounded quiet interval so voice packets that arrive behind their terminator can continue through the same decoder, TAR writer, and live lane without adding playout latency.
- Keep the visible Activity call anchored when newer calls are inserted above it, while continuing to follow new activity when already at the top, and wrap long Debug Log entries to the window width.

## [0.3.1] - 2026-08-19

### Added

- Add a default-on RX audio-processing control below the master output device. Enabled receive sessions use a classic LMR receiver post-decoder enhancement stage; disabling it restores TIA-102.BABA-A §1.12-faithful vocoder output.

### Changed

- Show each call's local date below its time in Event History.
- Apply an LMR receiver-style post-decoder enhancement stage to receive audio, with an additional 6 dB output gain for DMR, NXDN, and P25 Phase 2 while P25 Phase 1 retains the stage's default gain.
- Aggregate RX and TX vocoder level diagnostics over complete 8,000-sample windows instead of reporting one arbitrary 20 ms frame.

### Fixed

- Follow system-default microphone and speaker changes without restarting DVM Console, while preserving fixed-device routes and deferring microphone migration until an active PTT call ends.
- Keep the inspected Debug Logs row anchored while new entries are inserted above it, and let the window participate in normal application window stacking.
- Warm and drain the selected output route before the talk-permit cue, and briefly wait for a selected Bluetooth output that is changing profiles.
- Stop active TAR playback before deletion and marshal the resulting History/catalog changes back to the UI thread.
- Stop treating DMR voice burst sequence 2 as a privacy-indicator header when its payload happens to decode to the same slot-type value, preventing intermittent 60 ms receive dropouts.

## [0.3.0] - 2026-08-19

### Added

- Add an ordered custom tone-pattern editor with reusable 300–2500 Hz tone and silence steps that remain within one transmitted call.
- Add macOS-only actions for requesting Input Monitoring and microphone access from Console Settings.

### Changed

- Send P25 DTMF through the normal voice encoder, while decoded custom alert assets use corrected single-tone generation only for confidently detected sustained tones.
- Keep Quick Call II tone A and tone B in one call with frame-aligned setup and trailing time for reliable paging.
- Reduce the TAR Opus target bitrate from 16 kbps to 9 kbps while retaining VOIP mode and variable bitrate encoding.
- Embed TAR catalog metadata in each Opus recording instead of creating JSON sidecars, with verified migration of existing Opus sidecars without re-encoding audio.
- Promote the cross-platform solution to the repository root and relocate the
  live user guide under `docs/user-guide` for a standalone project layout.
- Restrict Avalonia developer diagnostics to Debug builds and exclude them from release packages.
- Consolidate build and packaging instructions in the live user guide and include the project and third-party license notices in every release package.

### Fixed

- Apply corrected P25 single-tone generation consistently to built-in alerts, the Tones panel, saved patterns, and both Quick Call II tones.
- Play the talk-permit cue only after the selected microphone produces audio, allowing Bluetooth headsets to finish switching profiles before the operator begins speaking.
- Keep the History date filters from overlapping at compact window sizes and apply the center detent while dragging slider thumbs with the mouse.
- Stabilize receiving channel cards by avoiding non-visual per-packet notifications, using render-only meter updates, and presenting DMR and P25 levels at the same PCM-based cadence.

### Removed

- Remove the retired Windows-only WPF project and its unused image and audio
  assets now that the Avalonia application is the standalone product.

## [0.2.4] - 2026-08-18

### Added

- Add per-frame vocoder erasure handling for damaged P25 DFSI records and missing or malformed NXDN voice packets so decoder concealment is included in live audio and TAR recordings.
- Add support for the current dvmhost NXDN FNE packet layout while retaining compatibility with legacy header-only packets.

### Changed

- Consolidate Event History into Console Settings, route the Activity header and TAR Viewer there, and return the Talkgroup Audio Recorder menu to Tools.
- Reveal a recording selected in History in Finder or Explorer instead of opening it in the operating system's media player.
- Keep the inspected History row anchored while incoming calls are inserted, while retaining live-follow behavior when already at the top.
- Fix P25 tone generation for alerts and signaling.

### Fixed

- Preserve usable P25 voice records when another record in the same LDU is damaged, keep encrypted IMBE keystream alignment across concealed slots, and advance missing LDU2 encryption state safely.
- Preserve NXDN audio cadence across bounded packet loss, reset privacy state after loss, and reject invalid frame offsets instead of decoding padding as voice.
- Keep P25 calls that use the FNE placeholder source ID visible in the Activity sidebar so their completed TAR recordings remain accessible.
- Remove sub-frame zero-duration History shells without discarding a playable recording that is finalized later.
- Remove the detached Event History window and its connected-traffic crash path.
- Initialize the consolidated History viewport only after its deferred tab content exists, and require an application-authored result from macOS package smoke tests.
- Start DMR receive state from voice LC headers, allow an explicit new header to reuse a recently ended stream ID, and retain those headers when bounded traffic queues shed stale voice.
- Keep receive-disabled channel cards out of the green audio-receive state, mirror active audio presentation across enabled copies of the same resource, and keep the indication active until queued audio reaches its terminator.
- Avoid treating recoverable FNE protocol-packet errors as connection loss, and restore the authoritative peer state after a transient status override.
- Surface bounded UI and decoder queue drops in receive diagnostics instead of silently discarding them.

## [0.2.3] - 2026-08-17

### Added

- Add zone-scoped enable-all and disable-all receive actions to the Channels menu.
- Add optional global keyboard PTT selection through F19, including F13–F19, and allow keyboard PTT to be disabled.
- Add receive-activity indicators for systems and zones, distinct system status accents, and center snapping for channel volume and balance.
- Add recording catalog search, filters, and technical details to Event History while keeping TAR setup and channel recording configuration in Recorder.

### Changed

- Present completed recordings in compact, space-efficient Event History rows with playback, open, and delete actions available inline.
- List Recorder channel configuration directly by system, expanded by default with each system independently collapsible.
- Keep standard slider thumbs without separate center markers while retaining the neutral snap behavior.
- Reconcile recording catalog changes incrementally in the background.
- Preserve one call identity across late, delayed, duplicate, and concurrent audio traffic, including independent output-device recovery.
- Calibrate built-in Alert 1 and Alert 3 tones to the requested frequencies, durations, vocoder windows, and −25 dBFS target.
- Preserve active transmit and receive paths while changing the warm-microphone setting.

### Fixed

- Prevent TX, PAGE, ALERT, and TAR card buttons from conflicting with card selection, flickering on hover, or losing their enabled colors while pressed.
- Prevent busy-system playback stalls, silent playable recordings, duplicate History rows, and stale recording-only entries.
- Preserve legacy recordings as stable, playable catalog entries when their sidecars are upgraded.
- Prevent concurrent History refreshes from racing on Windows and stopping the release build before packaging.
- Size codeplug and recording dialogs for long paths, keep Commands subscriber dialogs content-sized, and identify the macOS application as DVM Console.

## [0.2.2] - 2026-08-17

### Added

- Add persistent per-channel stereo balance controls to Console Settings so monitored channels can be routed left, center, or right without changing their configured loudness.
- Add an Activity sidebar control that filters Event History to the channels in the currently selected zone tab while retaining the system-wide Subscriber Command Audit.

### Changed

- Process receive audio independently per channel with bounded queues so one busy or delayed channel does not serialize other active calls.
- Mix receive audio as complete 20 ms frames and preserve each channel's configured level unless the combined PCM signal would overflow.
- Coalesce packet diagnostics, audio meters, and recording-catalog refreshes to keep the operator interface responsive during busy receive periods.

### Fixed

- Prevent garbled, stuttering receive audio and application lockups when many channels are active simultaneously.
- Keep mono output-device fallback audible regardless of a channel's stored stereo balance.
- Preserve DMR, P25, and NXDN call-lifecycle and encryption metadata when bounded receive queues discard stale voice traffic.

## [0.2.1] - 2026-08-17

### Changed

- Store talkgroup audio recordings as Ogg Opus and include the source radio ID in each recording filename.
- Show call security as Secure or Clear with compact algorithm labels, and use a standard play icon for playable recordings.
- Shorten the talk-permit tone while retaining startup and trailing silence for Bluetooth output reliability.
- Send generated alert, QCII, and DTMF audio through stable tone frames across supported digital voice modes.

### Fixed

- Make CLEAR transmissions unencrypted and SECURE transmissions use the channel's configured algorithm and key, with an orange all-caps SECURE state.
- Prevent the local talk-permit tone from entering transmitted microphone audio by discarding capture frames until the cue and its output tail have drained.
- Preserve the displayed color of channel-card controls while hovering or pressing, including SECURE and selected TX, PAGE, ALERT, and TAR states.
- Keep the AGC target value visible beside its spinner controls.

## [0.2.0] - 2026-08-17

### Added

- Add NXDN 4800-baud clear and encrypted voice receive and transmit, including EHR, DES, and AES-256 privacy.
- Add DMR ARC4, DES-OFB, and AES-256 privacy for receive and transmit using protocol-scoped local keys.
- Add an option to keep the transmit microphone warm, with a main-toolbar microphone toggle for changing the setting without opening Console Settings.
- Add high-quality AirPods input and output on supported macOS and AirPods combinations without requiring a separate helper application.
- Add an adjustable AGC target level while preserving the existing default.
- Add clearer guidance and gating for Apple Voice Processing routes that require the input and output to use the system-default pair or the same duplex device.
- Add detailed FNE and codec call diagnostics at Debug severity, with call start and end summaries at Info severity.
- Add 0.1-step controls for microphone gain, equalizer levels, and the AGC target.

### Changed

- Package Windows as one self-contained `DvmConsole.exe` with native components embedded.
- Derive the `DVMC_AV_<version>` FNE software identifier from the application version.
- Default the Debug Logs viewer to Info severity.
- Acquire high-quality Bluetooth audio only when the operator explicitly enables it.
- Play the talk-permit tone only after every selected transmit and audio path is ready.
- Apply AGC only to transmit microphone capture.
- Expand receive volume controls to expose the full supported range.
- Reorganize Console Settings tabs and streamline the Settings, Commands, View, and Channels menus.
- Show Console Settings scrollbars only while the window is actively scrolling.
- Clarify the built-in alert-tone tooltips with each tone's frequency and duration.
- Center text and content vertically and horizontally in buttons and editable fields.

### Fixed

- Prevent transmit startup and failure cleanup from terminating the desktop when background audio or FNE initialization completes outside the UI thread, while retaining the original startup failure in Debug Logs.
- Preserve half-rate forward-error-correction status through clear and encrypted DMR and NXDN receive paths, and conceal uncorrectable frames instead of decoding damaged voice data.
- Keep DMR privacy state synchronized across voice privacy headers, sequence gaps, and lost frames.
- Keep P25 encrypted receive fail-closed after loss and recover encryption state from subsequent signaling.
- Avoid retaining the local permit-tone output path so Apple Voice Processing can acquire its duplex route without the previous startup delay.
- Keep FNE connection-pill borders visible while hovering or pressing.
- Keep the Tones toolbar control clear of the Activity pane.
- Give the warm-microphone toolbar control a distinct orange enabled state and the normal button background when disabled.
- Restore microphone cleanup when the warm-microphone option is disabled or no longer needed.

## [0.1.1] - 2026-08-16

### Added

- Add AES-256-ECB and AES-256-CBC FNE transport framing, including automatic mode detection during login and explicit `auto`, `ecb`, and `cbc` codeplug options.

### Changed

- Keep the Debug Logs window modeless so operators can continue interacting with the main console while it is open.
- Rebind dependent views safely after reloading codeplug or application settings.

### Fixed

- Restore encrypted login compatibility with FNE deployments that use legacy ECB transport framing.
- Preserve P25 encryption state across each IMBE sequence so encrypted receive audio uses the configured algorithm and key before vocoder playback.
- Keep the final rows of every Console Settings tab scrollable above the status and Close footer.

## [0.1.0] - 2026-08-16

### Added

- Release the first public cross-platform Avalonia DVM Console for Apple Silicon macOS, Intel macOS, and Windows x64 as self-contained packages.
- Add simultaneous monitoring and transmission across multiple configured DVM FNE systems, with per-system connection status and indexed live-traffic routing.
- Add P25 and DMR audio encode and decode through the [dvmvocoder](https://github.com/DVMProject/dvmvocoder/tree/main) library, simultaneous receive mixing, per-channel volume, and output-device overrides.
- Add card, application, global-keyboard, and optional serial-device PTT.
- Add DVM Console microphone processing on macOS and Windows, plus Apple Voice Processing I/O on compatible macOS routes.
- Add built-in alerts, custom alert audio, DTMF, generated tones, and QCII pages for armed resources.
- Add patches, multi-select groups, call history, recordings, web streams, clocks, layouts, themes, startup behavior, and in-application operator documentation.
- Add support for local and KMM-provided P25 encryption keys while preserving compatibility with existing variable-length AES key material.

[Unreleased]: https://github.com/RdWing/dvmconsole/compare/v0.3.2...HEAD
[0.3.2]: https://github.com/RdWing/dvmconsole/compare/v0.3.1...v0.3.2
[0.3.1]: https://github.com/RdWing/dvmconsole/compare/v0.3.0...v0.3.1
[0.3.0]: https://github.com/RdWing/dvmconsole/compare/v0.2.4...v0.3.0
[0.2.4]: https://github.com/RdWing/dvmconsole/compare/v0.2.3...v0.2.4
[0.2.3]: https://github.com/RdWing/dvmconsole/compare/v0.2.2...v0.2.3
[0.2.2]: https://github.com/RdWing/dvmconsole/compare/v0.2.1...v0.2.2
[0.2.1]: https://github.com/RdWing/dvmconsole/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/RdWing/dvmconsole/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/RdWing/dvmconsole/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/RdWing/dvmconsole/releases/tag/v0.1.0
