# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Add an option to keep the transmit microphone warm, with a main-toolbar microphone toggle for changing the setting without opening Console Settings.
- Add high-quality AirPods input and output on supported macOS and AirPods combinations without requiring a separate helper application.
- Add an adjustable AGC target level while preserving the existing default.
- Add clearer guidance and gating for Apple Voice Processing routes that require the input and output to use the system-default pair or the same duplex device.

### Changed

- Apply AGC only to transmit microphone capture.
- Expand receive volume controls to expose the full supported range.
- Reorganize Console Settings tabs and streamline the Settings, Commands, View, and Channels menus.
- Show Console Settings scrollbars only while the window is actively scrolling.
- Clarify the built-in alert-tone tooltips with each tone's frequency and duration.

### Fixed

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

[Unreleased]: https://github.com/RdWing/dvmconsole/compare/v0.1.1...HEAD
[0.1.1]: https://github.com/RdWing/dvmconsole/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/RdWing/dvmconsole/releases/tag/v0.1.0
