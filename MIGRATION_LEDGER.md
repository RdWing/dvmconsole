# dvmconsole macOS Rebuild Ledger

## Scope

Rebuild the `r01a02_dev` desktop dispatch console for Apple Silicon macOS while retaining a Windows path. The new application will use a cross-platform .NET UI; the existing WPF application remains the Windows reference and fallback during migration.

## Decisions

- Target macOS Apple Silicon first: `osx-arm64`.
- Retain Windows support in the new application, initially targeting `win-x64`.
- Use the software `libvocoder` backend by default.
- Preserve a future `AMBE.DLL` backend behind a platform-neutral vocoder interface.
- Keep the rebuild on branch `codex/macos-cross-platform-rebuild`.
- Make each milestone a focused commit with an informative message.

## Plan

| ID | Milestone | Status | Exit criteria |
|---|---|---|---|
| M0 | Repository and ledger setup | Complete | Dedicated branch and checked-in ledger exist. |
| M1 | Cross-platform solution skeleton | Complete | New projects build without changing the legacy WPF project. |
| M2 | Configuration/core extraction | Complete | Codeplug, key, and alias models/loaders are covered by tests. |
| M3 | FNE core modernization | Complete | FNE protocol source builds against .NET 8 with offline smoke tests. |
| M4 | Software vocoder backend | Complete | `libvocoder` loads and encode/decode vectors pass on Apple Silicon with tracked tests. |
| M5 | Audio and platform services | In progress | Platform-neutral audio devices, capture, routing, and PTT contracts exist; macOS CoreAudio, Windows NAudio, manual PTT, and a lifecycle-safe keyboard PTT adapter are implemented, while hardware PTT adapters remain. |
| M6 | Avalonia application shell | In progress | Shared Avalonia shell starts on the desktop target, uses compiled bindings with typed view templates, is wired for Avalonia Developer Tools, shows codeplug-derived system/channel status in variable system-driven tabs, and exposes explicit live FNE connect/disconnect status; feature parity remains. |
| M7 | Feature migration | In progress | Platform-neutral FNE traffic, DMR/P25/analog RX packet extraction/selection and call lifecycle, DMR TX PCM/AMBE packet aggregation and explicit DMR call start/end packets, a DMR wire-packet builder, clear and key-file-backed P25 TX DFSI LDU1/LDU2 aggregation, TDU call lifecycle, capture orchestration, clear DMR/P25/analog Avalonia PTT policy, focused-channel keyboard PTT routing, controlled multi-channel clear DMR/P25/analog receive fan-in with fixed-rate sample mixing, bounded Avalonia call history, PCM/vocoder frame boundaries, Avalonia channel activity state wiring, explicit DMR capture/PTT lifecycle, P25 encrypted RX with codeplug key-file lookup, selectable P25 encryption with persisted operator state, persisted Avalonia startup/selection settings, an end-to-end analog μ-law transmit media/capture/PTT path, listened-channel streaming TAR WAV recording with metadata/retention/catalog and ignored-subscriber/playback/delete controls, legacy group normalization, live clear/MI-instruction P25 KMM key request/response handling, an explicit optional KMF secret boundary for peer-encrypted KMM, a live patch forwarding boundary with codeplug-derived patch editing and automatic supported-mode source capture, generated alert/DTMF transmission, local talk-permit tone playback, persisted dark-mode shell theming, per-channel receive-volume settings, an injectable NXDN media boundary, backward-compatible step-based tone/DTMF presets with hold timing, persisted input/output device identities, bounded microphone gain/AGC/EQ processing, configurable per-channel output routing, configurable per-stream output routing, configurable RX-mute-on-PTT with resume, persisted microphone presets, and a pluggable decoder boundary with HTTP(S) PCM-WAV, managed MP3, and opt-in FFmpeg web-stream playback exist; a production NXDN backend and remaining broader settings remain. |
| M8 | Packaging and integration handoff | In progress | Reproducible unsigned framework-dependent macOS and Windows publish paths exist; signing, installer packaging, and integration handoff remain. |

## Working assumptions

- The existing `dvmconsole/` WPF project is not modified until the new core boundaries are proven.
- Native dependencies are loaded through explicit runtime-aware adapters rather than hard-coded Windows filenames.
- Existing codeplug/configuration compatibility is more important than preserving the current WPF view structure.

## Activity log

### 2026-08-14

- Cloned `r01a02_dev` with the `fnecore` submodule into the workspace.
- Audited the branch: WPF/.NET 8 application, `fnecore` on `netcoreapp3.1`, Windows-specific audio and keyboard code, and `libvocoder`/`AMBE.DLL` interop.
- Verified the current `dvmvocoder` source builds as `libvocoder.dylib` on this Apple Silicon host.
- Created branch `codex/macos-cross-platform-rebuild`.
- Added the first .NET 8 projects: configuration core, vocoder abstraction, source-based FNE wrapper, bootstrap app, and core tests.
- Added `src/DvmConsole.Rebuild.sln` as the rebuild entry point.
- Reserved `configs/codeplug_testing.yml` for explicit live-FNE/live-codeplug validation; it is not used by automated tests.
- Extracted the legacy key-file and radio-alias loaders into the cross-platform core and covered them with repository fixtures.
- Added offline FNE protocol tests for RTP headers, FNE extension headers, fragmentation/reassembly, and opcode construction.
- Added tracked software-vocoder frame validation and native encode/decode tests for DMR AMBE and P25 IMBE; documented the external `dvmvocoder` build.
- Added platform-neutral audio contracts for device enumeration, PCM capture/playback, PTT state, and a tested assembler for arbitrary callback sizes to 160-sample vocoder frames.
- Added the first Avalonia desktop shell for the shared `net8.0` desktop target. It loads a codeplug, shows systems/zones/channels, and labels FNE connections offline until the connection service is migrated.
- Applied the Avalonia migration guidance to the desktop shell: enabled compiled bindings, added `x:DataType` declarations for the window and nested system/zone/channel templates, and wired the supported diagnostics package with its `AttachDeveloperTools()` hook. For this .NET 8 macOS target, installed the platform-specific `AvaloniaUI.DeveloperTools.macOS` tool (`avdt` 2.2.3); the generic package is intended for newer SDKs and was not the correct install target. The package's `WithDeveloperTools()` builder hook was not used because it duplicates the attachment and aborts startup with this package version.
- Added an explicit FNE client lifecycle service that maps legacy system configuration, resolves endpoints, configures `fnecore.FnePeer`, and publishes disconnected/starting/authentication/configuration/connected/faulted status states without auto-connecting.
- Added a bounded `DvmConsole.FneProbe` utility for explicit live testing; it does not run from the desktop app and redacts credentials/raw packets from output.
- Added an Apple Silicon CoreAudio/AudioUnit native shim, managed audio backend, streaming PCM rate converter, and `DvmConsole.AudioProbe`; device enumeration and a two-second default input/output stream test pass on this Mac.
- Added deterministic PCM rate-converter tests, bringing the audio test count to six, and kept the native audio library outside the managed solution build so Windows remains buildable while its backend is pending.
- Wired the Avalonia shell to the FNE lifecycle service with explicit Connect/Disconnect commands, per-system status updates, UI-thread dispatch, and clean window-shutdown disposal; startup remains idle until the operator connects.
- Added the Windows NAudio/WinMM audio implementation and runtime backend factory. It compiles with the cross-platform solution, but a physical Windows audio-device run remains outstanding.
- Added a copied, platform-neutral FNE traffic event boundary for DMR, P25, NXDN, and analog frames, plus streaming PCM-to-vocoder and vocoder-to-PCM processors for the next RX/TX routing stage.
- Continued the Avalonia vertical slice by forwarding normalized FNE traffic to matching channel view models on the UI thread. DMR channels match destination, zero-based slot, and voice frame; P25 channels match voice LDU1/LDU2; NXDN channels match destination. Matching cards now expose the shared channel runtime's receiving state; audio playback and PTT remain deliberately deferred.
- Added the first explicit Avalonia receive-audio path: `Listen` lazily selects the default output through `AudioBackendFactory`, loads `SoftwareVocoderBackend`, composes `ChannelReceiveAudioSession` for DMR/P25, routes matching FNE frames off the UI thread, and disposes the single active output session on stop or window close. Startup remains device- and vocoder-idle; NXDN and multi-channel mixing remain deferred.
- Added the reusable DMR transmit media seam: `DmrTxAudioSession` encodes 160-sample PCM frames, aggregates three AMBE codewords into the existing 55-byte packet builder, and emits explicit packet/stream metadata through a transport callback. Microphone capture, PTT orchestration, link-control headers, call start/end, and terminators remain deferred.
- Added the explicit DMR transmit call lifecycle: `DmrTxCallSession.Start()` emits a voice-LC header, voice packets carry embedded-LC sequencing, and `End()` emits a link-control terminator. The zero-based slot bit now matches the FNE wire convention, and RTP sequence advancement reserves `65535` for call-end signaling.
- Added the first host transmit boundary: configured system RIDs are carried separately from FNE peer IDs, `DmrTransmitCaptureSession` binds one input device to one DMR call, and the Avalonia channel card exposes explicit PTT/Release for non-RX-only DMR channels. Capture, vocoder, and input resources remain lazy and are cleaned up on stop, fault, or window close; P25/NXDN transmission remains disabled.
- Added clear P25 transmit media construction: `P25DfsiFrameCodec` now builds the legacy 200-byte LDU1/LDU2 DFSI layout from nine IMBE codewords, and `P25TxAudioSession` aggregates PCM into alternating clear LDUs with RTP sequencing. HDU/TDU call signaling and encryption/key management remain explicitly deferred, so P25 UI PTT/live transmit stays disabled.
- Added the clear P25 call lifecycle boundary: `P25TxCallSession` emits one grant-demand TDU before voice, alternates the clear LDU media session, and emits four legacy-compatible terminating TDUs on end. P25 HDU/encryption/key management and UI/live PTT remain disabled.
- Extended codeplug validation to reject zero/non-numeric destination IDs and invalid DMR slots before view-model construction.
- Added a lifecycle-safe manual PTT source with transition tests as the host-controlled foundation for future keyboard and hardware PTT adapters.
- Added the platform-neutral `KeyboardPttSource`: configurable Space/F-key activation, unrelated/repeat filtering, transition-only state events, and safe release on stop/dispose are covered without coupling the audio layer to Avalonia; channel selection and hardware input routing remain host work.
- Added `P25TransmitCaptureSession` for the clear protocol slice: capture starts after the grant-demand TDU, PCM is routed through the P25 call session, and stop/dispose reliably emits the four-TDU termination sequence; P25 encryption and desktop channel policy remain disabled.
- Propagated channel `algo`, `keyId`, and selectable-encryption metadata into `ChannelRuntimeDefinition`; the Avalonia PTT boundary now selects DMR or clear P25 capture/vocoder sessions and fails closed for encrypted or unknown algorithms. The example encrypted P25 channel remains visibly non-transmitting while clear channels are eligible.
- Added focused-channel keyboard PTT routing to the Avalonia shell: channel cards become focusable/selectable, tunnel `KeyDown`/`KeyUp` events map Space/F-keys to the platform-neutral adapter, and selection cannot change while the key is held. No selected channel means no keyboard transmission and the key is not consumed.
- Replaced the single active receive session with controlled multi-channel clear DMR/P25 fan-in: one shared playback device and vocoder backend can host multiple channel sessions, coordinator serialization prevents PCM interleaving, individual Listen/Stop is supported, and encrypted/unknown channels are rejected before audio infrastructure opens.
- Rebound the Avalonia channel tabs to codeplug systems: tab count and order now follow the configured systems, tab labels use system names, and channels from all zones are grouped under their configured system while preserving the existing zone model for compatibility.
- Expanded the system-tab regression fixture to cover multiple channels per system across multiple zones, preserving codeplug channel order, mode, and talkgroup identifiers.
- Added a fixed-rate shared PCM mixer for selected receive channels: each channel has an independent queue, active frames are summed with saturation, missing channel frames are treated as silence, and channel removal leaves the shared output alive until the final session stops.
- Added the initial analog receive playback seam: `analog` codeplug channels can be listened to without opening a vocoder, packets are selected by destination and voice frame, and decoded samples route through the shared mixer; NXDN media remains fail-closed.
- Corrected the analog media boundary against the current dvmhost source: `ANOD` packets are 344 bytes with G.711 μ-law in the first 160 bytes of the 320-byte audio region; the rebuild now decodes that wire format, keeps the reserved bytes intact, and updates the legacy packet-length/tag constants.
- Added the isolated analog transmit media seam: arbitrary capture callback sizes are assembled into 160-sample frames, packets emit `VOICE_START` then `VOICE`, optional grant demand is carried in the control byte, and call end emits an RTP call-end `TERMINATOR`; the desktop PTT wiring follows in the next activity entry.
- Enabled the analog transmit seam in the Avalonia PTT coordinator: analog channels now open the default capture device and send `ANOD` traffic without allocating a digital vocoder, while clear DMR/P25 and encrypted/NXDN policy boundaries remain unchanged.
- Added bounded Avalonia call history: the shell records one newest-first entry per new voice stream with system, channel, protocol, source, destination, stream ID, and local timestamp, trimming to 100 entries like the legacy call-history window.
- Added fail-closed P25 encrypted receive: the desktop loads the configured key file, resolves AES/DES-OFB/ARC4 keys by algorithm and key ID, extracts legacy HDU/LDU2 encryption metadata, decrypts consecutive LDU1/LDU2 IMBE codewords through `fnecore.P25Crypto`, and keeps encrypted transmit, live KMM, and encrypted DMR unsupported.
- Added a portable JSON `UserSettingsStore` with injected paths, atomic writes, and resilient malformed-profile fallback; the Avalonia shell now persists the last codeplug and selected channel and restores the selection when that channel still exists.
- Completed the settings startup path: launching without an explicit codeplug now restores the persisted last codeplug when available, with a regression test using an injected profile store.
- Persisted the selected dynamic system tab alongside the selected channel and bound the Avalonia `TabControl` to that system selection; desktop tests now use injected profile stores so verification cannot mutate the real operator profile.
- Audited current dvmhost NXDN framing: `NXDD` carries a raw 384-bit NXDN frame behind the network wrapper, while the native vocoder boundary currently exposes only DMR AMBE and P25 IMBE; NXDN receive therefore remains explicitly fail-closed with regression coverage rather than attempting an invalid decode.
- Added key-file-backed P25 encrypted transmit: the P25 media session encrypts each IMBE codeword with `fnecore.P25Crypto`, emits legacy HDU and LDU2 encryption-sync metadata, advances the MI between LDUs, and enables Avalonia PTT only when the configured P25 key resolves; DMR encryption and live KMM remain fail-closed.
- Added selectable P25 encryption in the Avalonia channel card: codeplug channels marked `selectable_encryption` can switch between clear and encrypted transmit before PTT, while the key remains required to enter the secure state and the choice cannot change during an active call.
- Added the live P25 KMM key-management boundary: connected systems request missing configured P25 algorithm/key IDs using the console RID, clear and MI-instruction responses are normalized into copied in-memory key material, channel capability state refreshes when a key arrives, and peer-encrypted KMM responses fail closed until a separate KMF secret boundary exists; raw KMM payloads and key material are not logged or persisted.
- Added the explicit optional KMF configuration boundary: `kmfPresharedKey` is carried separately from the FNE transport `presharedKey` and is passed to `fnecore` only for peer-encrypted KMM response decryption; the transport key is never reused implicitly.
- Persisted selectable P25 Secure/Clear operator state per channel in the atomic user-settings profile; only the boolean choice is stored, and restore remains gated by the current codeplug's `selectable_encryption` flag.
- Added receive call lifecycle closure: matching DMR/P25/NXDN/analog terminators return only the active stream to `Idle`, stale terminators cannot close a newer stream, and terminator packets never enter the voice decoders.
- Added the first TAR recording slice: a channel Record action lazily enables Listen, decoded 8 kHz PCM is tee’d into a streaming RIFF/WAVE writer, and one file per stream is finalized on terminator, audio stop, or shutdown under the application recording directory.
- Completed the next TAR boundary: finalized recordings now receive atomic JSON sidecars with stream/source/system/channel/protocol/encryption metadata, a safe catalog loader and seven-day retention prune remove paired WAV/sidecar files, and the Avalonia sidebar shows the completed recording catalog.
- Added persisted recording-retention days to the portable operator profile, preserving a seven-day default and clamping malformed negative values to the safe disabled-prune boundary.
- Restored legacy group compatibility in the extracted configuration model: current `groups` and legacy `patchGroups` are merged, deduplicated case-insensitively with current entries taking precedence, and normalized group types expose patch versus multiselect classification.
- Added a platform-neutral 8 kHz mono PCM tone generator with single- and dual-frequency synthesis tests; device playback, generated alert/DTMF controls, and reusable operator presets are layered above it.
- Added the first host patch-forwarding boundary: retained enabled patch memberships restore only against configured patch groups, source call/terminator lifecycle is routed through the platform-neutral table, and decoded listened-channel PCM is adapted to DMR/P25/analog target call sessions with source-ID passthrough and disconnect/shutdown cleanup.
- Added operator controls for ignored recording subscriber IDs and completed TAR catalog actions with safe in-root delete plus default-player open; invalid IDs are rejected before persistence and catalog sidecars remain paired.
- Added generated DTMF and alert-tone transmission controls: standard DTMF sequences and single-frequency tones are synthesized as bounded 8 kHz PCM, paced through the selected channel's normal DMR/P25/analog call lifecycle, persisted as operator inputs, and kept compatible with selectable clear/secure P25 state.
- Added the codeplug-derived Avalonia patch editor: patch memberships, one-way mode, enabled state, and startup retention are editable against the dynamic system/channel list and feed the existing transactional router.
- Added per-channel receive-volume settings: the shared PCM mixer applies independent live gain with saturation, channel sliders persist by stable system/channel key, and the codeplug-driven cards restore their saved values.
- Added automatic patch-source capture for enabled codeplug patch members: supported DMR/P25/analog source channels now decode through a silent PCM boundary and feed the existing patch router without opening operator playback; unresolved encrypted P25 and NXDN sources remain inactive until their required media boundary exists.
- Added portable generated-audio preset management: normalized DTMF and tone step stacks round-trip through the operator profile, legacy digit/frequency-only profiles migrate to one-step presets, hold timing is preserved, and the Avalonia sidebar can save, load, send, and delete them without persisting codeplug credentials or key material.
- Added the injectable NXDN media boundary: NXDD packet extraction, destination selection, and a 48-byte-frame-to-8 kHz PCM receive session are tested behind an explicit FEC/AMBE+2 decoder interface; the default desktop path remains fail-closed until a real NXDN backend is available.
- Added an operator-facing TAR retention control: the configured day count is editable from the Avalonia sidebar, persists through the portable profile, prunes the catalog immediately on apply, and accepts zero as an explicit disabled-pruning value.
- Added cross-platform microphone and output routing settings: input/output device IDs, bounded microphone gain, optional AGC, and low/mid/high EQ persist in the operator profile; the transmit capture path applies the processing before protocol encoding, and the shared receive mixer honors the selected output device with default fallback.
- Added the legacy RX-mute-on-PTT policy to the Avalonia profile: the default preserves the current safe muted behavior, operators can disable it, and suspended receive channels are restored after normal PTT completion or transmit failure.
- Added per-channel receive output routing: channel settings persist a stable output device ID, the channel card can override the master output route, and the receive coordinator creates/disposes independent mixers per selected output while retaining default fallback.
- Added per-stream receive output routing and microphone presets: each configured web stream can persist an output-device override, while bounded microphone gain/EQ combinations can be named, saved, loaded, and deleted through the Avalonia sidebar without persisting credentials or key material.
- Wired optional codeplug-relative radio alias files into the Avalonia runtime: aliases load when present, source status shows `alias (RID)`, and channels without an alias retain the numeric source display.
- Added codeplug web-stream validation: stream names are globally unique and URLs must be absolute HTTP(S), matching the legacy resource validation boundary before any future cross-platform stream playback is opened.
- Added the portable web-stream decoder slice: configured HTTP(S) resources can use Basic auth, auto-detect PCM WAV or MPEG audio, decode WAV with the in-box reader or MP3 through the managed MIT NLayer adapter, route each stream to the selected output device, restore per-stream volume, and stop cleanly; other compressed formats remain explicitly unsupported.
- Added startup resource-selection parity: the portable profile can opt in or out of restoring the selected channel and active web streams, active stream names persist through start/stop transitions, and matching configured streams are restored asynchronously by the Avalonia shell.
- Rechecked the production NXDN dependency boundary against the built `libvocoder.dylib`: it exports only DMR AMBE/P25 IMBE session entry points, while NXDN is present only as AMBE FEC helper code; no valid production NXDN decoder can be enabled without adding a separate AMBE+2 implementation.
- Added legacy talk-permit tone parity: the Avalonia profile persists the option, PTT start schedules a 50 ms local 1200 Hz tone through the configured output device, and the player is isolated from radio transmit media with fake-backend coverage.
- Added Avalonia dark-mode parity: the operator profile persists the choice, the shell switches the application theme variant, and sidebar/card surfaces use light/dark theme dictionaries instead of fixed light-only colors.
- Corrected channel validation for dynamic system tabs: channel-name uniqueness is now scoped to the configured system, so independent systems may each define a channel with the same display name while duplicate runtime keys within one system remain rejected.
- Added a reproducible desktop publishing script and publishing guide. It builds framework-dependent `osx-arm64` and `win-x64` outputs and places the macOS CoreAudio shim beside the macOS managed output.
- Extended desktop publishing to co-locate the optional native vocoder when `DVMVOCODER_LIBRARY` is supplied, using the platform-specific filename expected by the runtime loader; a published macOS folder now contains both native runtime libraries and launches against the supplied codeplug.
- Added the first end-to-end DMR RX media slice: the legacy 55-byte DMR FNE voice-packet layout is mapped to three AMBE codewords, decoded through the software-vocoder boundary, and written to the platform-neutral playback boundary. Non-voice frames are ignored; P25 DFSI reconstruction and call/channel selection remain deliberately above this reusable session.
- Added reusable DMR receive selection and routing: destination ID, zero-based FNE timeslot, and voice frame type are matched before decode, and selected packet processing is serialized for one playback path.
- Added P25 DFSI receive decoding for complete LDU1/LDU2 packets: the nine IMBE codewords are reconstructed from records `0x62–0x6A` and `0x6B–0x73`, selected by talkgroup, and sent through the software-vocoder playback boundary. Key management/decryption is explicitly deferred so encrypted traffic is not silently treated as clear audio.
- Added the first outbound traffic seam: DMR AMBE codewords can be mapped back into a tested 55-byte voice packet, and `FneConnection.SendTraffic` now owns protocol-opcode dispatch without exposing transport construction to the desktop layer. DMR link-control headers, packet sequencing, terminators, and PTT orchestration remain outstanding.
- Re-ran the supplied live FNE probe after attaching the traffic boundary; the system reached `Connected` in five seconds and exited successfully after clean shutdown.
- Retried the supplied live FNE codeplug after macOS Local Network permission was granted. The endpoint `10.10.10.55:62031` exchanged traffic, completed login/authentication, reached `Connected`, and shut down cleanly. The probe now preserves the observed-connected result after shutdown and exits successfully; diagnostic packet tracing is opt-in and sanitized.
- Verified three configuration tests, four FNE protocol tests, the bootstrap against `configs/codeplug.example.yml`, the full solution with `/m:1`, and the native vocoder smoke harness.
- Recorded the FNE wrapper's .NET 8 compatibility warnings as follow-up modernization debt; the original `fnecore` source remains unchanged.

## Commit ledger

| Commit | Purpose |
|---|---|
| `7dc3cc3` | Initial branch and migration ledger. |
| `d298080` | Cross-platform .NET 8 project skeleton and configuration/vocoder boundaries. |
| `056870b` | Legacy key and alias loading extracted into the cross-platform core. |
| `6142c29` | Offline FNE protocol test project and solution integration. |
| `808d66a` | Software-vocoder frame validation, native tests, and build documentation. |
| `dc4da04` | Platform-neutral audio contracts and PCM frame assembler. |
| `7364d22` | Initial Avalonia macOS/Windows desktop shell. |
| `0fd5f7b` | Explicit FNE connection lifecycle service and offline tests. |
| `e752a65` | Bounded live-FNE probe, testing documentation, and clean socket shutdown handling. |
| `83c727f` | Record the first bounded live-FNE probe result. |
| `321d059` | Record the pre-CoreAudio rebuild verification baseline. |
| `6223429` | Apple Silicon CoreAudio/AudioUnit backend, rate conversion, and stream probe. |
| `f1b3944` | Opt-in FNE diagnostics, stable connected-state reporting, and live-probe success tracking. |
| `f31e3f3` | Explicit Avalonia FNE connect/disconnect commands and lifecycle status display. |
| `4cff086` | Windows NAudio/WinMM audio backend and runtime backend factory. |
| `b0ab7f8` | Platform-neutral DMR/P25/NXDN/analog FNE traffic event boundary. |
| `e9d0646` | Streaming PCM/vocoder frame encoder and decoder pipeline. |
| `caca017` | Manual PTT source lifecycle and transition tests. |
| `4420bd0` | Reproducible Apple Silicon/Windows desktop publishing script and guide. |
| `b99390c` | Record publishing and manual-PTT milestones. |
| `e030bc5` | Record traffic-pipeline verification. |
| `548e0a0` | Record the platform-neutral audio boundary milestone. |
| `fe3c246` | Record the Avalonia shell milestone. |
| `9d18b95` | Record the FNE client milestone. |
| `4711b33` | Record the full-solution verification baseline. |
| `730a165` | Record CoreAudio and live-FNE milestones. |
| `9b46790` | DMR RX packet extraction, software-vocoder decode, playback session, and focused media tests. |
| `c13ecb8` | DMR receive channel selection and serialized playback routing. |
| `46443d3` | P25 DFSI LDU reconstruction, IMBE extraction, and clear receive playback tests. |
| `7ab68f3` | DMR AMBE wire-packet builder and protocol-neutral FNE send boundary. |

## Verification log

| Check | Result |
|---|---|
| Host architecture | Apple Silicon / `arm64` |
| .NET SDK | 9.0.300; .NET 8 SDK also installed |
| Native vocoder CMake build | Passed in temporary verification build |
| Native vocoder .NET smoke test | Passed on Apple Silicon; 0 decode errors |
| Core configuration tests | 3 passed |
| FNE protocol tests | 4 passed on Apple Silicon; legacy compatibility warnings remain |
| Software vocoder tests | 4 passed on Apple Silicon with `DVMVOCODER_LIBRARY=/private/tmp/dvmvocoder-build/libvocoder.dylib` |
| Audio framing tests | 3 passed on Apple Silicon |
| PCM rate-converter tests | 6 passed on Apple Silicon |
| Native macOS audio shim | Passed CMake arm64 build; device enumeration passed for input/output devices |
| macOS audio stream probe | Passed default input/output stream test; captured 16,043 target-rate PCM samples over two seconds |
| Rebuild solution after audio boundary | Passed with `dotnet build src/DvmConsole.Rebuild.sln --no-restore /m:1` (14 legacy FNE warnings) |
| Avalonia desktop shell | Built cleanly; launch check remained running as expected until the test process was interrupted |
| FNE connection service tests | 5 passed without opening a network connection |
| Rebuild solution after FNE client | Passed with `dotnet build src/DvmConsole.Rebuild.sln --no-restore /m:1` (14 legacy FNE warnings) |
| Live FNE probe | Supplied private testing codeplug validated; 1 system reached `WaitingForLogin`, no `Connected` state in 10 seconds; clean shutdown, expected nonzero result |
| Live FNE probe after Local Network permission | Passed: 1 system exchanged traffic with `10.10.10.55:62031`, reached `Connected` within 10 seconds, shut down cleanly, and returned exit code 0 |
| Live FNE probe after traffic boundary | Passed: 1 system reached `Connected` within five seconds with traffic subscriptions attached, shut down cleanly, and returned exit code 0 |
| Avalonia shell startup | Passed: launched with `configs/codeplug.example.yml` and remained running until intentionally interrupted |
| Avalonia typed-shell build | Passed: compiled bindings and all typed system/zone/channel templates build with 0 warnings and 0 errors |
| Avalonia shell launch smoke test | Passed: launched with `configs/codeplug.example.yml` and shut down cleanly when interrupted; FNE remained idle until operator action |
| Windows audio runtime | Not run on Windows hardware; compile verification passed on macOS |
| Desktop publish script | Passed framework-dependent `osx-arm64` and `win-x64` publishes; macOS output includes `libdvmaudio.dylib` |
| Final solution test run before media slice | 26 passed: Core 3, FNE 4, Vocoder 6, Audio 8, FNE client 5 (native vocoder included) |
| Media test project | 9 passed: DMR packet/decode/routing/round-trip coverage plus P25 LDU1/LDU2 extraction and nine-frame playback |
| Final solution test run after media slice | 29 passed: Core 3, FNE 4, Vocoder 6, Audio 8, FNE client 5, Media 3 (native vocoder included) |
| Final solution build after media slice | Passed all 16 solution projects with `/m:1`; 0 warnings and 0 errors |
| Bootstrap example validation | Passed: 1 system and 3 zones loaded from `configs/codeplug.example.yml` |
| Rebuild solution | Passed with `dotnet build src/DvmConsole.Rebuild.sln --no-restore /m:1` |
| Bootstrap config validation | Passed with `configs/codeplug.example.yml` |
| Live testing config | Present locally and ignored by Git |
| Legacy application build on macOS | Not attempted; WPF is Windows-only |
| Rebuild solution after Avalonia guidance | Passed: all 16 projects build with 0 warnings and 0 errors; 38 tests pass with `DVMVOCODER_LIBRARY` set to the local Apple Silicon library |
| Avalonia Developer Tools installation | Passed: `AvaloniaUI.DeveloperTools.macOS` 2.2.3 installed, `/Users/jchang/.dotnet/tools/avdt --help` runs, and the tool directory is now on the zsh PATH |
| Avalonia channel activity slice | Passed: matching DMR and P25 traffic updates channel runtime state; wrong system/destination/slot/non-voice traffic is ignored |
| Desktop channel view-model tests | Passed: 3 tests covering DMR matching/rejection and P25 LDU activity |
| Rebuild solution after Avalonia channel activity slice | Passed: all 17 projects build with 0 warnings and 0 errors; 41 tests pass with `DVMVOCODER_LIBRARY` set to the local Apple Silicon library; desktop launch smoke test passed |
| Avalonia receive-audio slice | Passed: explicit Listen/Stop lifecycle composes DMR/P25 decode and playback without auto-start; 45 total solution tests pass with `DVMVOCODER_LIBRARY` set to the local Apple Silicon library |
| DMR transmit media seam | Covered: PCM-to-AMBE aggregation emits sequenced 55-byte voice packets through a callback; capture and call-control integration remain deferred |
| Rebuild solution after DMR transmit media seam | Passed: all 17 projects build with 0 warnings and 0 errors; 47 tests pass with `DVMVOCODER_LIBRARY` set to the local Apple Silicon library |
| DMR transmit call lifecycle | Passed: header, embedded-LC voice sequencing, terminator, slot-bit, and RTP-wrap tests pass; no microphone/PTT or live transmit integration is enabled |
| Rebuild solution after DMR transmit call lifecycle | Passed: all 17 projects build with 0 warnings and 0 errors; 50 tests pass with `DVMVOCODER_LIBRARY` set to the local Apple Silicon library |
| DMR capture/PTT boundary | Passed: fake capture test verifies header-before-capture, PCM routing, terminator-on-release, RID/source mapping, RX-only gating, and startup remains input-idle |
| Rebuild solution after DMR capture/PTT boundary | Passed: all 17 projects build with 0 warnings and 0 errors; 52 tests pass with `DVMVOCODER_LIBRARY` set to the local Apple Silicon library |
| Avalonia PTT startup smoke test | Passed: desktop launched with `configs/codeplug.example.yml` and was interrupted cleanly; no input/vocoder activation occurs before PTT |
| P25 clear TX media slice | Passed: legacy DFSI LDU1/LDU2 record layouts round-trip nine IMBE codewords, alternate with RTP sequencing, and reject incomplete identifiers/input through the reusable media session |
| Rebuild solution after P25 clear TX media slice | Passed: all 17 projects build with 0 warnings and 0 errors; 53 tests pass with `DVMVOCODER_LIBRARY` set to the local Apple Silicon library; DMR wire-codec tests are serialized around the legacy shared scratch state |
| P25 clear call lifecycle slice | Passed: grant-demand TDU, alternating LDU1/LDU2 voice packets, four terminating TDUs, reserved RTP call-end sequencing, and explicit start/process/end guards are covered |
| Rebuild solution after P25 clear call lifecycle slice | Passed: all 17 projects build with 0 warnings and 0 errors; 55 tests pass with `DVMVOCODER_LIBRARY` set to the local Apple Silicon library; `git diff --check` passes |
| Keyboard PTT adapter slice | Passed: configurable activation key, unrelated/repeat filtering, transition-only events, and release on stop are covered by 2 focused tests |
| Rebuild solution after keyboard PTT adapter slice | Passed: all 17 projects build with 0 warnings and 0 errors; 57 tests pass with `DVMVOCODER_LIBRARY` set to the local Apple Silicon library; `git diff --check` passes |
| P25 capture orchestration slice | Passed: synthetic capture verifies TDU-before-capture ordering, two clear LDUs from PCM, and four terminating TDUs on stop |
| Rebuild solution after P25 capture orchestration slice | Passed: all 17 projects build with 0 warnings and 0 errors; 58 tests pass with `DVMVOCODER_LIBRARY` set to the local Apple Silicon library; `git diff --check` passes |
| Clear transmit policy slice | Passed: channel encryption metadata is preserved, clear P25 is eligible, and encrypted/unknown DMR/P25 channels are denied by view-model policy |
| Rebuild solution after clear transmit policy slice | Passed: all 17 projects build with 0 warnings and 0 errors; 61 tests pass with `DVMVOCODER_LIBRARY` set to the local Apple Silicon library; `git diff --check` passes |
| Avalonia startup after clear transmit policy slice | Passed: `configs/codeplug.example.yml` launched and was interrupted cleanly; input/vocoder activation remained PTT-gated |
| Focused-channel keyboard PTT slice | Passed: Avalonia channel cards are focusable/selectable and tunnel keyboard events route only to the selected channel; no-channel input remains unconsumed |
| Avalonia startup after focused-channel keyboard PTT slice | Passed: `configs/codeplug.example.yml` launched and was interrupted cleanly after key-routing handlers were attached |
| Multi-channel receive fan-in slice | Passed: injected-backend coverage starts two clear DMR sessions on one playback stream, routes both independently, stops one without disposing the shared output, and fails closed for encrypted receive |
| Rebuild solution after multi-channel receive fan-in slice | Passed: all 17 projects build with 0 warnings and 0 errors; 63 tests pass with `DVMVOCODER_LIBRARY` set to the local Apple Silicon library; `git diff --check` passes |
| Avalonia startup after multi-channel receive fan-in slice | Passed: `configs/codeplug.example.yml` launched and was interrupted cleanly; shared playback/vocoder infrastructure remained Listen-gated |
| System-driven Avalonia tabs and channels | Passed: two-system fixture produces two tabs in codeplug order, places five channels across those systems and two zones, and preserves each channel's configured mode/talkgroup; full solution build is clean and 64 tests pass with `DVMVOCODER_LIBRARY` set to the local Apple Silicon library |
| Multi-channel PCM mixer | Passed: focused mixer tests verify saturated summing and independent channel removal; coordinator lifecycle coverage passes; full solution build is clean and 66 tests pass with `DVMVOCODER_LIBRARY` set to the local Apple Silicon library |
| Analog receive media slice | Corrected and passed: current dvmhost-compatible 344-byte `ANOD` packet extraction, G.711 μ-law decode, destination/frame selection, direct PCM playback, no-vocoder startup, and listen-only UI policy are covered; the reserved portion of the audio region is not decoded |
| Avalonia call history slice | Passed: bounded newest-first storage, one-entry-per-stream integration, typed sidebar rendering, and stream metadata are covered; full solution build is clean and 74 tests pass with `DVMVOCODER_LIBRARY` set to the local Apple Silicon library |
| P25 encrypted receive slice | Passed: configured AES/DES-OFB/ARC4 key lookup, legacy LDU1 HDU and LDU2 encryption-sync extraction, consecutive LDU1/LDU2 decryption, desktop Listen eligibility, coordinator startup, and missing-key fail-closed behavior are covered; full solution build is clean and 83 tests pass with `DVMVOCODER_LIBRARY` set to the local Apple Silicon library |
| Avalonia operator settings slice | Passed: atomic JSON persistence, missing/malformed fallback, last-codeplug persistence and pathless-startup restore, and selected-channel persistence/restore are covered; the full solution test run passes 91 tests with `DVMVOCODER_LIBRARY=/private/tmp/dvmvocoder-build/libvocoder.dylib` |
| Dynamic system-tab selection persistence | Passed: the Avalonia `TabControl` is bound to the codeplug-derived system list and persists/restores the selected system name together with the selected channel |
| NXDN media prerequisite audit | Passed: the rebuild preserves NXDN channel visibility and matching but rejects audio session creation until an NXDN FEC/AMBE+2/vocoder boundary is available; no raw 384-bit frame is treated as PCM or DMR AMBE |
| Analog wire-format compatibility slice | Passed: `ANOD` tag/header fields, 344-byte packet size, μ-law encode/decode, and reserved audio/trailer bytes are covered; its desktop capture/send lifecycle is covered below |
| Analog transmit media seam | Passed: PCM framing, `VOICE_START`/`VOICE` sequencing, optional grant-demand control, capture lifecycle, μ-law packetization, and RTP call-end terminator coverage pass; desktop integration is covered by the subsequent coordinator slice |
| Analog desktop transmit integration | Passed: analog channels are PTT-eligible when clear and not RX-only, the coordinator selects `FneTrafficProtocol.Analog` and `AnalogTransmitCaptureSession`, and no software vocoder is created for analog calls |
| Rebuild solution after analog compatibility/transmit slices | Passed: all projects build with 0 warnings and 0 errors in the incremental verification run; 89 solution tests pass with `DVMVOCODER_LIBRARY=/private/tmp/dvmvocoder-build/libvocoder.dylib`; `git diff --check` passes |
| Rebuild solution after settings startup restore | Passed: all projects build with 0 warnings and 0 errors; 91 solution tests pass with `DVMVOCODER_LIBRARY=/private/tmp/dvmvocoder-build/libvocoder.dylib` |
| Latest Avalonia shell startup smoke | Passed after dynamic selected-tab binding: the supplied `configs/codeplug_testing.yml` launched and remained running with the Avalonia native backend until intentionally interrupted; the process exited with the expected Ctrl-C code 130 and no render-timer failure occurred |
| Avalonia pathless startup smoke | Passed: launching without a command-line codeplug restored the persisted profile and remained running until intentionally interrupted; the process exited with the expected Ctrl-C code 130 |
| Supplied live-testing codeplug validation | Passed: `DvmConsole.App` loaded `configs/codeplug_testing.yml` with 1 system and 1 zone and reported configuration validation passed; the desktop smoke remained FNE-idle until operator action |
| Bounded live FNE probe with supplied codeplug | Passed: `configs/codeplug_testing.yml` reached `Connected` for `TEST FNE` at the configured endpoint, stayed connected for the 10-second probe, and shut down cleanly with exit code 0; no console traffic was sent |
| Rebuild solution after analog desktop transmit integration | Passed: all projects build with 0 warnings and 0 errors; 91 solution tests pass with `DVMVOCODER_LIBRARY=/private/tmp/dvmvocoder-build/libvocoder.dylib`; `git diff --check` passes |
| Supplied codeplug smoke after analog desktop transmit integration | Passed: `DvmConsole.App` validated `configs/codeplug_testing.yml`, the Avalonia shell remained running until intentional Ctrl-C, and the bounded live FNE probe reached `Connected` and shut down cleanly |
| P25 encrypted transmit slice | Passed: deterministic encrypted LDU1/LDU2 generation decrypts back to the original IMBE codewords, LDU1 carries the legacy HDU metadata, LDU2 carries the cycled next MI, and encrypted P25 PTT eligibility remains key-file-gated |
| Rebuild solution after P25 encrypted transmit slice | Passed: all projects build with 0 warnings and 0 errors; 92 solution tests pass with `DVMVOCODER_LIBRARY=/private/tmp/dvmvocoder-build/libvocoder.dylib`; `git diff --check` passes |
| Supplied codeplug smoke after P25 encrypted transmit slice | Passed: `DvmConsole.App` validated `configs/codeplug_testing.yml` and the Avalonia shell remained running until intentional Ctrl-C; no transmit was initiated |
| Selectable P25 encryption slice | Passed: a key-backed `selectable_encryption` channel exposes a Secure/Clear toggle, starts encrypted by default, permits clear PTT after toggling, and keeps channels without a resolved key unavailable |
| Live P25 KMM key-management boundary | Passed: connected-state/source-ID/algorithm/key-ID request validation, mutable key-ring add/replace cloning, dynamic channel capability refresh, and fail-closed peer-encrypted KMM handling are covered; no raw KMM payload or key material is logged or persisted, and the supplied live probe remained control-only |
| Optional KMF secret boundary | Passed: configuration and connection options preserve a distinct `kmfPresharedKey`; peer-encrypted KMM remains fail-closed without it and is handed to the legacy decryptor only when explicitly configured; no supplied live secret was used in verification |
| Selectable encryption operator-state persistence | Passed: the Secure/Clear choice is stored as a per-channel boolean in the atomic user-settings profile, restored only for codeplug channels marked `selectable_encryption`, and never stores encryption key material |
| Rebuild solution after KMM/KMF boundary | Passed: all projects build with 0 warnings and 0 errors; 96 solution tests pass with `DVMVOCODER_LIBRARY=/private/tmp/dvmvocoder-build/libvocoder.dylib`; `git diff --check` passes |
| Rebuild solution after selectable encryption persistence | Passed: all projects build with 0 warnings and 0 errors; 98 solution tests pass with `DVMVOCODER_LIBRARY=/private/tmp/dvmvocoder-build/libvocoder.dylib`; `git diff --check` passes |
| Receive call lifecycle closure | Passed: matching terminators close the active stream, stale terminators are ignored, and the desktop test suite covers the state transition; no terminator is routed to media decoding |
| TAR streaming WAV recording slice | Passed: 16-bit PCM RIFF finalization, no-overwrite behavior, per-stream file creation, channel Record command wiring, terminator finalization, and shutdown/stop cleanup are covered |
| TAR metadata, retention, and catalog slice | Passed: completed receive recordings write atomic JSON sidecars, catalog loading filters missing/out-of-root files, retention removes expired WAV/sidecar pairs, and the Avalonia sidebar renders recording metadata |
| TAR ignored-subscriber and playback/delete controls | Passed: ignored subscriber IDs normalize and persist through the channel editor, rejected IDs never enter the profile, recording files open through the platform shell only after in-root validation, and paired WAV/sidecar deletion is covered |
| Legacy group normalization slice | Passed: current and `patchGroups` entries merge with current-name precedence, names/types normalize, and patch/multiselect classification is covered by core tests |
| Host patch forwarding boundary | Passed: retained enabled memberships restore only for configured patch groups, source call/terminator lifecycle reaches the tested router, and the coordinator adapts both automatically decoded enabled patch sources and listened decoded PCM to clear DMR/P25/analog transmit sessions with source-ID passthrough and cleanup on membership change/disconnect/shutdown; unsupported NXDN and unresolved encrypted P25 sources remain fail-closed |
| Portable tone and retention settings slice | Passed: operator retention days round-trip through the atomic profile, malformed negative values clamp safely, the Avalonia sidebar can apply 0–3650-day pruning including disabled pruning, and platform-neutral single/dual PCM tone generation is covered |
| Generated alert/DTMF controls | Passed: standard DTMF digit mapping, sequence silence gaps, single-frequency alert generation, selected-channel DMR/P25/analog pacing, selectable clear/secure P25 definition handling, persisted operator tone inputs, and save/load/delete preset controls are covered |
| NXDN media boundary | Passed: the 70-byte NXDD layout extracts only its raw 48-byte frame, an injected NXDN decoder can produce one 160-sample PCM frame, and the default channel receive path still rejects NXDN without that decoder |
| Radio alias runtime slice | Passed: configured alias files resolve relative to the codeplug, load into the system configuration, and matching channel source state displays the alias with the numeric RID while preserving numeric fallback |
| Web-stream configuration validation | Passed: configured stream names are checked for duplicates and stream URLs are restricted to absolute HTTP/HTTPS endpoints; the supplied example remains valid |
| Dynamic system/channel identity validation | Passed: equal channel display names are accepted across different systems and rejected only when their system-scoped runtime identity collides |
| Codeplug-derived patch editor | Passed: patch-group UI models only configured patch groups, exposes dynamic channel membership with one-way/enabled controls, restores enabled retained state, and applies normalized memberships through the transactional router |
| Per-channel receive volume | Passed: independent shared-mixer gain, saturation, startup restore, slider persistence, and coordinator-level configured gain are covered |
| Rebuild solution after automatic patch-source capture | Passed: all projects build with 0 warnings and 0 errors; 132 solution tests pass with `DVMVOCODER_LIBRARY=/private/tmp/dvmvocoder-build/libvocoder.dylib`; focused automatic-source tests cover supported DMR decoding without an audio backend and fail-closed unsupported/unresolved sources |
| Rebuild solution after preset/NXDN/retention slices | Passed: all projects build with 0 warnings and 0 errors; 134 solution tests pass with `DVMVOCODER_LIBRARY=/private/tmp/dvmvocoder-build/libvocoder.dylib`; supplied codeplug validation passes and `git diff --check` is clean |
| Rebuild solution after alias/resource/system-identity slice | Passed: all projects build with 0 warnings and 0 errors; 138 solution tests pass with `DVMVOCODER_LIBRARY=/private/tmp/dvmvocoder-build/libvocoder.dylib` |
| Rebuild solution after step-based preset slice | Passed: all projects build with 0 warnings and 0 errors; 141 solution tests pass with `DVMVOCODER_LIBRARY=/private/tmp/dvmvocoder-build/libvocoder.dylib`; `git diff --check` passes |
| Rebuild solution after audio device/processing slice | Passed: all projects build with 0 warnings and 0 errors; 144 solution tests pass with `DVMVOCODER_LIBRARY=/private/tmp/dvmvocoder-build/libvocoder.dylib`; supplied codeplug validation passes and `git diff --check` is clean |
| Rebuild solution after RX-mute policy slice | Passed: all projects build with 0 warnings and 0 errors; 144 solution tests pass with `DVMVOCODER_LIBRARY=/private/tmp/dvmvocoder-build/libvocoder.dylib` |
| Rebuild solution after per-channel output routing slice | Passed: all projects build with 0 warnings and 0 errors; 145 solution tests pass with `DVMVOCODER_LIBRARY=/private/tmp/dvmvocoder-build/libvocoder.dylib`; focused routing tests cover separate and shared output lifecycles |
| Rebuild solution after patch/TAR control slices | Passed: all projects build with 0 warnings and 0 errors; 119 solution tests pass with `DVMVOCODER_LIBRARY=/private/tmp/dvmvocoder-build/libvocoder.dylib`; `git diff --check` passes |
| Supplied codeplug after patch/TAR control slices | Passed: `DvmConsole.App` loads `configs/codeplug_testing.yml` with 1 system and 1 zone and reports configuration validation passed; the control-only live probe reaches Connected and shuts down cleanly |
| Rebuild solution after TAR/group/tone slices | Passed: all projects build with 0 warnings and 0 errors; 107 solution tests pass with `DVMVOCODER_LIBRARY=/private/tmp/dvmvocoder-build/libvocoder.dylib`; `git diff --check` passes |
| Supplied codeplug validation after TAR/group/tone slices | Passed: `DvmConsole.App` loaded `configs/codeplug_testing.yml` with 1 system and 1 zone and reported configuration validation passed |
| Supplied codeplug validation/probe after KMM/KMF boundary | Passed: `configs/codeplug_testing.yml` validated, `TEST FNE` reached `Connected` for the 10-second control-only probe, and shutdown returned 0 without sending console media or key requests |
| Current Avalonia desktop smoke retry | Environment-limited: the process exited before window creation with Avalonia.Native render-timer error `-6661`; the same codepath had a prior successful shell smoke, and this retry did not reach codeplug/FNE startup |
| Native-library publish handoff | Passed: `scripts/publish-desktop.sh osx-arm64` built the arm64 audio shim and published `libdvmaudio.dylib` plus `libvocoder.dylib` when `DVMVOCODER_LIBRARY` was provided; the published Avalonia assembly remained running against `configs/codeplug_testing.yml` until intentional interruption |
| Windows x64 publish handoff | Passed: `scripts/publish-desktop.sh win-x64` produced the framework-dependent Windows runtime output on the macOS build host; Windows hardware/audio runtime remains untested here |
| Web-stream playback slice | Passed: injected HTTP(S) source coverage decodes PCM WAV, applies persisted volume, selects the requested output device, disposes playback cleanly, and reports compressed WAV as explicitly unsupported; the decoder contract remains replaceable for a future MP3/stream backend |
| Rebuild solution after web-stream playback slice | Passed: all projects build with 0 warnings and 0 errors; 150 solution tests pass with `DVMVOCODER_LIBRARY=/private/tmp/dvmvocoder-build/libvocoder.dylib`; `git diff --check` passes |
| Supplied codeplug validation after web-stream playback slice | Passed: `DvmConsole.App` loaded `configs/codeplug_testing.yml` with 1 system and 1 zone and reported configuration validation passed |
| Managed MP3 web-stream decoder slice | Passed: the adaptive decoder identifies ID3/MPEG input, NLayer produces mono PCM at the source rate from an embedded MP3 fixture, unknown compressed signatures fail explicitly, and the reader remains behind `IAudioPcmStreamReader` |
| Rebuild solution after managed MP3 decoder slice | Passed: all projects build with 0 warnings and 0 errors; 152 solution tests pass with `DVMVOCODER_LIBRARY=/private/tmp/dvmvocoder-build/libvocoder.dylib`; `git diff --check` passes |
| Per-stream output routing and microphone preset slice | Passed: web streams restore/save per-stream output-device overrides, microphone presets normalize and round-trip through the profile, and the Avalonia view-model covers save/use/delete behavior |
| Rebuild solution after per-stream output routing and microphone preset slice | Passed: all projects build with 0 warnings and 0 errors; 153 solution tests pass with `DVMVOCODER_LIBRARY=/private/tmp/dvmvocoder-build/libvocoder.dylib`; `git diff --check` passes |
| Supplied codeplug validation after per-stream output routing and microphone preset slice | Passed: `DvmConsole.App` loaded `configs/codeplug_testing.yml` with 1 system and 1 zone and reported configuration validation passed |
| Startup resource-selection parity slice | Passed: the restore preference and selected web-stream names normalize and round-trip through the atomic profile; the Avalonia view-model gates selected-channel restore and registers stream start/stop persistence |
| Rebuild solution after startup resource-selection parity slice | Passed: all projects build with 0 warnings and 0 errors; 153 solution tests pass with `DVMVOCODER_LIBRARY=/private/tmp/dvmvocoder-build/libvocoder.dylib`; `git diff --check` passes |
| Supplied codeplug validation after startup resource-selection parity slice | Passed: `DvmConsole.App` loaded `configs/codeplug_testing.yml` with 1 system and 1 zone and reported configuration validation passed |
| Production NXDN backend audit | Passed: the built native vocoder exports DMR AMBE and P25 IMBE session APIs but no NXDN decode session; the application remains explicitly fail-closed rather than routing NXDD frames into an incompatible mode |
| Talk-permit tone slice | Passed: the setting round-trips through the operator profile, PTT wiring schedules local output-only playback, and the isolated tone player emits a bounded 400-sample 8 kHz signal on the requested device |
| Rebuild solution after talk-permit tone slice | Passed: all projects build with 0 warnings and 0 errors; 154 solution tests pass with `DVMVOCODER_LIBRARY=/private/tmp/dvmvocoder-build/libvocoder.dylib`; `git diff --check` passes |
| Supplied codeplug validation after talk-permit tone slice | Passed: `DvmConsole.App` loaded `configs/codeplug_testing.yml` with 1 system and 1 zone and reported configuration validation passed |
| Published Avalonia handoff after talk-permit tone slice | Passed: the current `osx-arm64` publish contains `NLayer.dll`, `libdvmaudio.dylib`, and `libvocoder.dylib`; the published Avalonia process remained running against `configs/codeplug_testing.yml` until intentional Ctrl-C |
| Dark-mode shell slice | Passed: `DarkMode` round-trips through user settings, the Avalonia view-model updates `Application.RequestedThemeVariant`, and themed shell/card resources compile and are covered by focused Core/Desktop tests |
| Rebuild solution after dark-mode shell slice | Passed: all projects build with 0 warnings and 0 errors; 154 solution tests pass with `DVMVOCODER_LIBRARY=/private/tmp/dvmvocoder-build/libvocoder.dylib`; `git diff --check` passes |
| Supplied codeplug validation after dark-mode shell slice | Passed: `DvmConsole.App` loaded `configs/codeplug_testing.yml` with 1 system and 1 zone and reported configuration validation passed |
| NXDN fail-closed messaging slice | Passed: desktop receive-audio rejection now states the required injected FEC/AMBE+2 decoder boundary, with regression coverage confirming no audio or vocoder infrastructure is opened |
| Rebuild solution after NXDN fail-closed messaging slice | Passed: all projects build with 0 warnings and 0 errors; 155 solution tests pass with `DVMVOCODER_LIBRARY=/private/tmp/dvmvocoder-build/libvocoder.dylib`; `git diff --check` passes |
| Toolbar clock settings parity slice | Passed: the portable profile persists 12/24-hour and seconds visibility settings, the Avalonia header refreshes the local clock every second, and culture-aware formatting is covered by desktop tests |
| Rebuild solution after toolbar clock settings parity slice | Passed: all projects build with 0 warnings and 0 errors; 156 solution tests pass with `DVMVOCODER_LIBRARY=/private/tmp/dvmvocoder-build/libvocoder.dylib`; `git diff --check` passes |
| Supplied codeplug validation after toolbar clock settings parity slice | Passed: `DvmConsole.App` loaded `configs/codeplug_testing.yml` with 1 system and 1 zone and reported configuration validation passed |
| Injected NXDN backend orchestration slice | Passed: `ChannelReceiveAudioCoordinator` now accepts an optional available `INxdnVocoderBackend`, routes NXDD frames only through its session, and disposes it with the audio lifecycle; the default desktop path remains fail-closed |
| Rebuild solution after injected NXDN backend orchestration slice | Passed: all projects build with 0 warnings and 0 errors; 157 solution tests pass with `DVMVOCODER_LIBRARY=/private/tmp/dvmvocoder-build/libvocoder.dylib`; `git diff --check` passes |
| Supplied codeplug validation after injected NXDN backend orchestration slice | Passed: `DvmConsole.App` loaded `configs/codeplug_testing.yml` with 1 system and 1 zone and reported configuration validation passed |
| Window-shell settings parity slice | Passed: `KeepWindowOnTop` round-trips through the portable profile and binds to Avalonia `Topmost` with an operator checkbox |
| Rebuild solution after window-shell settings parity slice | Passed: all projects build with 0 warnings and 0 errors; 157 solution tests pass with `DVMVOCODER_LIBRARY=/private/tmp/dvmvocoder-build/libvocoder.dylib`; `git diff --check` passes |
| Supplied codeplug validation after window-shell settings parity slice | Passed: `DvmConsole.App` loaded `configs/codeplug_testing.yml` with 1 system and 1 zone and reported configuration validation passed |
| Optional FFmpeg web-stream decoder slice | Passed: the decoder preserves WAV/MP3 fast paths, invokes an explicitly configured `DVM_FFMPEG` process for Ogg/Opus input, converts stdout to 8 kHz mono PCM, and disposes the process/source lifecycle cleanly |
| Rebuild solution after optional FFmpeg web-stream decoder slice | Passed: all projects build with 0 warnings and 0 errors; 158 solution tests pass with `DVMVOCODER_LIBRARY=/private/tmp/dvmvocoder-build/libvocoder.dylib`; `git diff --check` passes |
| Supplied codeplug validation after optional FFmpeg web-stream decoder slice | Passed: `DvmConsole.App` loaded `configs/codeplug_testing.yml` with 1 system and 1 zone and reported configuration validation passed |
| Toggle-PTT settings parity slice | Passed: the keyboard PTT adapter persists the reference toggle preference, ignores auto-repeat keydown events in toggle mode, and retains hold-to-talk behavior by default |
| Rebuild solution after toggle-PTT settings parity slice | Passed: all projects build with 0 warnings and 0 errors; 159 solution tests pass with `DVMVOCODER_LIBRARY=/private/tmp/dvmvocoder-build/libvocoder.dylib`; `git diff --check` passes |
| Supplied codeplug validation after toggle-PTT settings parity slice | Passed: `DvmConsole.App` loaded `configs/codeplug_testing.yml` with 1 system and 1 zone and reported configuration validation passed |
| Publish artifact verifier slice | Passed: added `scripts/verify-publish.sh` and documented pre-handoff checks for managed runtime files, the arm64 macOS audio shim, optional native vocoder architecture, platform mismatches, and accidental testing/credential-like configuration material |
| Rebuild solution after publish verifier slice | Passed: all projects build with 0 warnings and 0 errors; 159 solution tests pass with `DVMVOCODER_LIBRARY=/private/tmp/dvmvocoder-build/libvocoder.dylib`; asynchronous web-stream lifecycle coverage now waits for terminal completion and `git diff --check` passes |
| Supplied codeplug and publish verification after publish verifier slice | Passed: `DvmConsole.App` loaded `configs/codeplug_testing.yml` with 1 system and 1 zone and reported configuration validation passed; the current `osx-arm64` publish passed the artifact verifier and `native/dvmaudio/build` remains absent |
| Serial hardware PTT adapter slice | Passed for the software boundary: added a cross-platform `SerialPttSource` with explicit line-state parsing, fail-safe release on EOF/stop/fault, injectable stream transport, `System.IO.Ports` support, and opt-in Avalonia wiring through `DVM_PTT_SERIAL_PORT`/`DVM_PTT_SERIAL_BAUD`; 44 Audio tests cover parsing, transitions, and EOF release; physical-device validation remains |
| Rebuild solution after serial hardware PTT slice | Passed: all projects build with 0 warnings and 0 errors; 171 solution tests pass with `DVMVOCODER_LIBRARY=/private/tmp/dvmvocoder-build/libvocoder.dylib`; `git diff --check` passes |
| Supplied codeplug and publish checks after serial hardware PTT slice | Passed: `DvmConsole.App` loaded `configs/codeplug_testing.yml` with 1 system and 1 zone and reported configuration validation passed; the current `osx-arm64` publish passed the artifact verifier and `native/dvmaudio/build` remains absent |
