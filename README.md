<div align="center">

<img src="repo/brand/dvm-console-neo-mark-color.svg" alt="DVM Console NEO signal-lane mark" width="112" height="112">

# DVM Console NEO

### Built for busy systems.

An open-source DVM FNE operator console for macOS and Windows—live channels,
patches, tones, recordings, and diagnostics in one dense workspace.

For amateur and educational use. **Not for public- or life-safety operation.**

[![Latest release](https://img.shields.io/github/v/release/RdWing/dvmconsole?display_name=tag&sort=semver&style=flat-square&color=0969da)](https://github.com/RdWing/dvmconsole/releases/latest)
[![Build and package](https://img.shields.io/github/actions/workflow/status/RdWing/dvmconsole/build.yml?branch=neo&style=flat-square&label=build)](https://github.com/RdWing/dvmconsole/actions/workflows/build.yml)
[![macOS 14+](https://img.shields.io/badge/macOS-14%2B-181717?style=flat-square&logo=apple)](#download-dvm-console)
[![Windows x64](https://img.shields.io/badge/Windows-x64-0078D4?style=flat-square&logo=windows)](#download-dvm-console)
[![License: AGPL-3.0](https://img.shields.io/badge/license-AGPL--3.0-6f42c1?style=flat-square)](LICENSE)

[Download](https://github.com/RdWing/dvmconsole/releases/latest) ·
[User guide](docs/user-guide/Getting%20Started/01-Overview.md) ·
[What’s new](#whats-new-in-dvm-console-neo) ·
[Changelog](CHANGELOG.md) ·
[Issues](https://github.com/RdWing/dvmconsole/issues)

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="repo/neo-dark.png">
  <source media="(prefers-color-scheme: light)" srcset="repo/neo-light.png">
  <img alt="DVM Console NEO showing three channel cards and the Activity sidebar" src="repo/neo-dark.png" width="100%">
</picture>

<sub>Public example configuration shown; no operational system data is included.</sub>

</div>

## Built for real operator workflows

DVM Console NEO keeps systems, zones, talkgroups, transmit routes, pages,
alerts, and recordings in one focused desktop workspace. Each channel remains
independently controllable. Global and active-system PTT handle broader routing,
while adaptive receive timing and stream-isolated audio are designed to keep
multi-system activity understandable.

| Operator need | NEO capability |
| --- | --- |
| **Maintain live RX continuity** | Independent receive work per channel, stream-isolated call state, and adaptive packet-aligned jitter handling for P25, DMR, and NXDN. |
| **Quiet the room without losing evidence** | Mute all RX, the selected system, or the selected zone without stopping decode, call state, patching, or TAR recording. |
| **Control exactly what transmits** | Per-channel, global, active-system, patch, and multi-select routing with explicit PTT, TX, PAGE, and ALERT selection. |
| **Investigate receive problems** | Activity and Event History, embedded recording metadata, redacted Debug Log export, and separate network, decoder, mixer, and output-device diagnostics. |
| **Use the supported operator workstation** | Self-contained packages for Apple Silicon macOS, Intel macOS, and Windows x64. No separate .NET runtime is required. |

## What’s new in DVM Console NEO

### 0.4.0 — Runtime and workflow update

This release debuts a revised Settings window, introduces responsive toolbar
behavior, adds optional engineering diagnostics, and optimizes the runtimes
that power DVM Console NEO.

- The operator interface uses freeform channel cards and the Activity sidebar.
- **View > Engineering Health** opens an optional diagnostics pane.
- Settings use searchable left navigation.
- DMR and P25 calls use mode-correct startup, pacing, and termination, while
  cross-protocol and all-to-all patches preserve each destination protocol.
- Cold Bluetooth PTT waits for the first post-transition microphone callback
  and measured output presentation latency before releasing operator audio.
- Runtime ownership, receive scheduling, settings persistence, recording
  finalization, and microphone health checks have been revised without changing
  supported codeplugs or settings.
- Unanswered FNE login requests retain the normal first retry and then back off
  to a maximum 60-second interval until connection or an operator restart.

[Read the 0.4.0 release notes →](docs/releases/v0.4.0.md)

### Prior recent improvements

Recent 0.3.8, 0.3.7, 0.3.6, and 0.3.5 updates also introduced:

- **0.3.8 — Receive continuity and audio-path optimization:** drained ended receive streams before cleanup, paced TAR and web-stream PCM, retired failed shared outputs, and standardized macOS microphone processing.
- **0.3.7 — Patch routing and Apple audio reliability:** made one-way patch direction explicit, prevented duplicate patch PCM, compacted group editing, isolated patch teardown, and bounded audio recovery and Quit.
- **0.3.6 — Windows audio and recording-history reliability:** replaced the legacy Windows WinMM backend with shared, event-driven WASAPI; added optional endpoint-provided communications processing; and kept completed TAR recordings available after their live History rows expire.
- **0.3.5 — Optimization and lifecycle reliability:** reduced managed allocation in receive, vocoder, and transmit paths; kept Activity, PTT, audio services, tools, and background work attached to the correct session through reload and shutdown; and clarified internal ownership without changing operator workflows.

[Read the 0.3.8 release notes →](docs/releases/v0.3.8.md) · [Read the 0.3.7 release notes →](docs/releases/v0.3.7.md) · [Read the 0.3.6 release notes →](docs/releases/v0.3.6.md) · [Read the 0.3.5 release notes →](docs/releases/v0.3.5.md)

## Download DVM Console NEO

Published release packages are self-contained. Download the package for the
destination computer and extract the entire archive before starting DVM Console
NEO. Version 0.4.0 packages use the filenames below. Until that version appears
on the Releases page, use the assets attached to the current published release.

| Platform | Package | Requirements |
| --- | --- | --- |
| Apple Silicon Mac | `dvmconsole-0.4.0-osx-arm64.zip` | macOS 14 or newer |
| Intel Mac | `dvmconsole-0.4.0-osx-x64.zip` | macOS 14 or newer |
| Windows PC | `dvmconsole-0.4.0-win-x64.zip` | Windows x64 |

**[Download the latest release →](https://github.com/RdWing/dvmconsole/releases/latest)**

> [!IMPORTANT]
> DVMHost/FNE R06A00 or newer is recommended. DVMConsole R02A00 has limited
> backwards compatibility with older FNE builds and older codeplugs. Review
> codeplugs created for R01A00 before using them with R02A00.

<details>
<summary><strong>Install on macOS</strong></summary>

1. Extract the complete ZIP and move `DVMConsole.app` to `Applications`.
2. The current package is unsigned. For an archive downloaded from the RdWing
   GitHub Release, remove its quarantine attribute:

   ```sh
   xattr -dr com.apple.quarantine "/Applications/DVMConsole.app"
   ```

3. Open DVM Console NEO normally and use **Open Codeplug** to load `codeplug.yml`.

macOS may request local-network access for FNE, microphone access for PTT, and
Accessibility or Input Monitoring access for OS-global PTT.

If the application closes immediately, preserve
`~/Library/Application Support/DVMProject/dvmconsole/LastCrash.log` before
starting it again.

</details>

<details>
<summary><strong>Install on Windows x64</strong></summary>

1. Choose **Extract All** in File Explorer.
2. Start the self-contained `DvmConsole.exe` from the extracted folder.
3. Use **Open Codeplug** to load `codeplug.yml`.

If Microsoft Defender SmartScreen warns about the unsigned package, continue
only after confirming that the archive came from an RdWing GitHub Release.
If the application closes unexpectedly, preserve
`%APPDATA%\DVMProject\dvmconsole\LastCrash.log` before starting it again.

</details>

## Operator capabilities

- Monitor and transmit on DMR, P25 Phase 1, and NXDN 4800 FNE talkgroups.
- Organize resources by system and zone with per-channel receive and routing.
- Key individual channels, every TX-selected channel, or TX-selected channels
  in the active system using on-screen, keyboard, or configured serial PTT.
- Build patch and multi-select groups without losing independent channel state.
- Send DTMF, generated tones, Quick Call II pages, saved tone patterns, and
  custom alert audio through selected resources.
- Record talkgroup audio locally in Ogg Opus with catalog metadata embedded in
  each recording.
- Use P25 FNE/KMM key delivery with a local fallback, plus protocol-scoped local
  privacy keys for DMR and NXDN.
- Follow system-default audio devices or pin fixed microphone and speaker routes.
- Use DVM Console microphone processing on macOS and Windows, with optional
  device-dependent Windows communications processing on supported endpoints.

> [!NOTE]
> DVM Console NEO connects to DVM FNE peers. It does not directly control base or
> mobile radios. NXDN 9600/EFR and P25 Phase 2 transport are not implemented.

For a DVM-compatible console that supports direct base or mobile radio
interfaces, see [RadioConsole2](https://github.com/W3AXL/RadioConsole2) and
[rc2-dvm](https://github.com/W3AXL/rc2-dvm).

## Start with the guide

| If you want to… | Read… |
| --- | --- |
| Understand systems, zones, channels, and the main console | [Overview](docs/user-guide/Getting%20Started/01-Overview.md) |
| Create a codeplug and connect to FNE | [Codeplug creation](docs/user-guide/Getting%20Started/03-Configurations/01-Codeplug%20Creation.md) |
| Configure PTT, routes, History, and operator controls | [Console operation](docs/user-guide/Getting%20Started/04-Operations/01-Console%20Operation.md) |
| Configure microphones, speakers, microphone processing, and RX processing | [Audio settings](docs/user-guide/Getting%20Started/04-Operations/03-Audio%20Settings.md) |
| Configure encryption and inspect key status | [Encryption keys](docs/user-guide/Getting%20Started/03-Configurations/02-Encryption%20Keys.md) |
| Configure and manage local recordings | [Talkgroup Audio Recorder](docs/user-guide/Getting%20Started/03-Configurations/05-Talkgroup%20Audio%20Recorder.md) |
| Build or package the application | [Building and packaging](docs/user-guide/Getting%20Started/02-Building.md) |

`Help > Documentation` opens the operator guide.

## Open source and project lineage

DVM Console NEO is an independently maintained downstream of the original
[DVMProject/dvmconsole](https://github.com/DVMProject/dvmconsole) codebase. The
NEO releases in this repository are maintained by RdWing and are not official
DVMProject releases or an assertion of DVMProject endorsement.

The project is developed in public under the AGPL-3.0-only license. Use GitHub
Issues for reproducible defects, Discussions for setup and field-testing
questions, and GitHub private vulnerability reporting for security reports.

## Build from source

The repository targets .NET 10 and includes a native Rust component. Install
the .NET 10 SDK, Rust 1.85 or newer, CMake, and the platform C/C++ toolchain.

```sh
git clone --recurse-submodules https://github.com/RdWing/dvmconsole.git
cd dvmconsole
git submodule update --init --recursive
dotnet restore dvmconsole.sln
dotnet build dvmconsole.sln
```

Use the root `dvmconsole.sln`. Native components are built automatically; the
repository scripts own publishing, package verification, and macOS smoke tests.
The network-disabled deterministic demo is available with `--demo`.

## Network, configuration, and safety

> [!WARNING]
> The current FNE plaintext and legacy encrypted transports are compatibility
> protocols, not mutually authenticated sessions. Use FNE only across a trusted
> network or an authenticated VPN.

The tracked files under `configs` are public examples. Operational codeplugs,
clear key files, aliases, recordings, crash logs, and exported diagnostics may
contain private information and should not be committed.

### Configuration support policy

Project maintainers cannot validate configurations generated, rewritten,
modified, or "fixed" by AI/LLM tools such as ChatGPT, Copilot, Gemini, Claude,
or similar services.

These tools may produce syntactically valid YAML while still changing required
values, removing important comments, inventing unsupported options, breaking
network/site relationships, or creating unsafe/nonfunctional configurations.

If an AI/LLM tool was used to read, modify, or generate a configuration, disclose
that material use and reproduce the problem with a human-reviewed configuration
before requesting help.

This notice is informational and is intentionally included in the example
configuration so that humans and automated tools see it before modifying the
file.

> DVMHost/FNE R06A00 or newer is recommended.

DVM Console NEO is for amateur and educational use. It is not for public- or
life-safety operation.

## License

DVM Console NEO is free software licensed under the
[GNU Affero General Public License, version 3](LICENSE). Third-party license
terms and notices distributed with the project are included in that file.
