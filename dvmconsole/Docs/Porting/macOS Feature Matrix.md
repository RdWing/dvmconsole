# macOS Feature Matrix

<!-- SPDX-License-Identifier: AGPL-3.0-only -->

This matrix describes the Avalonia/macOS shell relative to the WPF `r01a02_dev`
operator surface. A row marked **UNVERIFIED** has a Linux/build or source
contract but still needs a real macOS host run. It is not a claim of hardware
or radio success.

The packaging targets are `osx-arm64` and `osx-x64`. Development bundles can
include `Contents/Frameworks/libvocoder.dylib`; the native library must match
the selected architecture for vocoder-backed voice operation.
Use `packaging/macos/build-vocoder.sh` to produce that library before
`packaging/macos/build-app.sh`; the resulting development bundle is unsigned
and unnotarized until the later signing pipeline runs on a Mac.

| Area | Avalonia/macOS state | Evidence or limitation |
| --- | --- | --- |
| Codeplug open and reload | Implemented | File → Open Codeplug parses before replacing the live runtime; reload preserves the existing runtime on failure. |
| Settings transfer/reset | Implemented | Settings transfer window supports category selection and merge-preserving import/export; reset remains confirmation-gated where exposed. |
| Debug Logs | Implemented | Help → Debug Logs opens the bounded viewer with copy, save, clear, secret redaction and scrollbars. |
| About and documentation | Implemented | App → About reports release/hash, runtime/OS/architecture and native-vocoder readiness. Help → Documentation opens the published documentation tree in the host browser. |
| CoreAudio input/output | Implemented, host-dependent | macOS CoreAudio adapters are composed at startup; device loss/replug behavior requires host validation. |
| Vocoder loading | Implemented, host-dependent | Packaged bundles resolve `Contents/Frameworks/libvocoder.dylib`; missing native exports are reported as unavailable. |
| FNE connection and voice | Implemented, host-dependent | Managed transport and voice seams are composed; real DMR/P25 RX/TX still require a controlled macOS/FNE fixture. |
| Global hotkeys | Implemented, permission-dependent | macOS uses CGEventTap and reports permission-required rather than prompting or bypassing TCC. |
| Accessibility/Input Monitoring | **UNVERIFIED** | The permission model and diagnostics are implemented; clean-account grant/deny/restart behavior needs a macOS acceptance run. |
| Microphone permission | **UNVERIFIED** | The bundle declares microphone usage; actual prompt and capture behavior need a macOS host run. |
| TAR recording/viewer | Partial / **UNVERIFIED** | Viewer and WAV boundaries exist; decoded RX/TX lifecycle and real recordings require host evidence. |
| Patch groups | Partial / **UNVERIFIED** | Group settings/editor and shell composition exist; two-radio or wire-observed forwarding is not proven on Linux. |
| Alert tones and DTMF | Partial / **UNVERIFIED** | Managers and preset flows exist; real target dispatch and wire-side audio require host evidence. |
| Web streams | Partial / **UNVERIFIED** | Stream source/session and shell chips exist; Basic Auth, decoder, retry and output-device replug need macOS evidence. |
| P25 encryption/key UX | Not complete | Subscriber security/key runtime remains a later milestone and must not be inferred from clear-voice support. |
| Signing/notarization | Not complete | `packaging/macos/build-app.sh` produces unsigned development bundles; Developer ID signing and notarization are user-owned. |

## Runtime paths

The default macOS application data root for `UserSettings.json` is:

```text
~/Library/Application Support/DVMProject/dvmconsole/
```

The Avalonia shell first checks `<application-data>/codeplug.yml`. When it is
absent, startup falls back to `Environment.CurrentDirectory/configs/codeplug.yml`;
File → Open Codeplug accepts an explicit path. System aliases are loaded from
the configured `Codeplug.System.AliasPath` (default `./alias.yml`) and are not
automatically remapped into Application Support. Debug Logs are a bounded,
in-memory `LogBuffer`; Save writes a user-selected snapshot. TAR recordings
default below the user's Documents folder. A packaged app therefore needs
explicit codeplug and alias paths if the repository checkout is not present.

## Known limitations

- Linux CI cannot prove CoreAudio, TCC prompts, microphone capture, browser
  launch, FNE radio reception, or radio audibility. Report wire-side results
  separately from unverified radio reception.
- Unsigned bundles may be blocked by Gatekeeper. Clear quarantine for local
  development only, or use Finder's Open flow; production signing is a later
  packaging gate.
- The published documentation opener requires a host browser and degrades
  silently if no browser is available. The bundle itself does not embed a
  Markdown renderer.
- `r01a02_dev` does not provide an operational NXDN voice path; NXDN is not
  listed as a missing Avalonia feature.
