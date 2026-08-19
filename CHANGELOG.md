# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/RdWing/dvmconsole/compare/v0.2.4...HEAD
[0.2.4]: https://github.com/RdWing/dvmconsole/compare/v0.2.3...v0.2.4
[0.2.3]: https://github.com/RdWing/dvmconsole/compare/v0.2.2...v0.2.3
[0.2.2]: https://github.com/RdWing/dvmconsole/compare/v0.2.1...v0.2.2
[0.2.1]: https://github.com/RdWing/dvmconsole/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/RdWing/dvmconsole/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/RdWing/dvmconsole/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/RdWing/dvmconsole/releases/tag/v0.1.0
