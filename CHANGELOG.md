# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/RdWing/dvmconsole/compare/v0.2.2...HEAD
[0.2.2]: https://github.com/RdWing/dvmconsole/compare/v0.2.1...v0.2.2
[0.2.1]: https://github.com/RdWing/dvmconsole/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/RdWing/dvmconsole/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/RdWing/dvmconsole/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/RdWing/dvmconsole/releases/tag/v0.1.0
