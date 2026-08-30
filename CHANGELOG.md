# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.5.5] - 2026-08-30

### Fixed

- Finish a canceled web-stream start in the `Off` state even when cancellation
  completes before the concurrent stop operation publishes `Stopping…`.
- Give the Bluetooth post-cue recovery regression test enough wall-clock
  allowance to remain deterministic on contended CI runners without changing
  the production recovery deadline.

## [0.5.4] - 2026-08-30

### Changed

- Use one receive-stream state machine for live allocation-free processing and
  immutable policy evaluation.
- Drive FNE connection state and login retry cadence from typed peer events
  instead of parsing diagnostic log messages.
- Give audio stream readers an explicit single-consumer contract with bounded
  cancellation and disposal behavior.
- Add receive-episode identity to new TAR metadata while retaining bounded
  timestamp reconciliation for older recordings.

### Fixed

- Make Configuration Studio flush current editor state before planning and
  saving, reject duplicate destination paths, preserve restricted permissions
  on staged secrets and backups, and synchronize saved settings with the live
  application snapshot.
- Normalize configuration tokens during validation, reject ports that cannot
  reserve the required metadata port, preserve empty known YAML collections,
  and prevent vendor fields from being assigned by list position.
- Let Stop and disposal cancel a web stream that is still opening without
  holding the playback gate across network, decode, or audio-device work.
- Correlate rapid recordings with the exact receive episode, keep encryption
  filters from treating unknown calls as clear or secure, and apply the host
  filesystem's path-comparison rules to recording containment checks.
- Return pending compatible-UDP receives during shutdown, retain exponential
  FNE login backoff without relying on log text, and keep application-owned FNE
  code under nullable and analyzer coverage.
- Reset late-entry NXDN privacy state correctly, flush padded partial analog
  frames before call termination, and prevent failed PCM frame handoffs from
  being replayed.
- Keep mixer lanes alive until the physical output accepts their final samples,
  and release native macOS audio streams and Bluetooth overrides after startup,
  pump, stop, or disposal failures.
- Reallocate the native CoreAudio capture ring after the negotiated sample rate
  is known, and build and test the native audio component in macOS CI.
- Marshal post-await desktop state changes back to the UI dispatcher and report
  settings persistence failures instead of allowing them to escape UI event
  handlers.

## [0.5.3] - 2026-08-29

### Changed

- Treat each FNE-sourced talkgroup table as authoritative for console,
  multi-channel, tone, page, alert, DTMF, and patch transmission.
- Match DMR authority by talkgroup and timeslot. Match P25, NXDN, and analog
  authority by destination ID.

### Fixed

- Prevent a locally configured DMR channel from keying on a timeslot that the
  FNE advertises for the other slot. Invalid targets now disable PTT and produce
  an operator warning before any call-start or terminator traffic is sent.
- Stop active console and patch transmissions cleanly when a refreshed FNE
  table removes their target, and restore availability when later rules permit
  it again.
- Reset talkgroup authority on disconnect and publish authority changes in
  order, so stale session callbacks cannot re-enable or disable targets after a
  replacement connection takes ownership.
- Validate and parse FNE talkgroup activation and deactivation announcements at
  the transport boundary without repeating inbound frame validation in later
  protocol handlers.

## [0.5.2] - 2026-08-29

### Added

- Add a General setting that disables the attenuated local monitor for
  generated tones, DTMF, presets, and QCII pages without changing transmitted
  audio.

### Changed

- Apply Configuration Studio group membership, direction, and enabled state as
  operator settings without rewriting YAML, disconnecting FNE sessions, or
  reloading the codeplug. YAML definition changes continue through Review &
  Save.
- End patch forwarding at the ordered receive-worker boundary so PCM already
  held by the adaptive jitter buffer is processed before a confirmed terminator
  closes the source.

### Fixed

- Release P25 physical receive streams promptly after a confirmed terminator,
  including short calls that have not filled the live-playback startup cushion.
  Voice that definitively restarts a reused stream ID receives a fresh decoder.
- Release failed patch-target state before reporting the failure so the next
  source audio block can establish a replacement outbound session reliably.
- Keep system and zone mute scopes on every live-speaker admission path,
  including session startup and receive restoration after transmit. Muted
  resources continue decoding and recording through TAR without opening a
  speaker lane.
- Serialize FNE connect, disconnect, and replacement transitions; cancel an
  in-progress start before disconnecting; and quiesce an outgoing session before
  the replacement owns its peer identity.
- Ignore session-owned UI callbacks after disposal starts and bound the entire
  application shutdown sequence instead of only the final session cleanup.
  Quiesce FNE sessions first so a failed peer's exponential login retry cannot
  hold Quit open for its current retry interval.

## [0.5.1] - 2026-08-29

### Changed

- Give patch forwarding an explicit delayed-start cadence factory, document that
  cadence state belongs to one serial transmit stream, and cover timer
  conversion with a non-tick timestamp frequency.

### Fixed

- Keep outbound 20 ms audio frames on absolute deadlines when Windows timer
  wakeups run late. Small delays no longer accumulate into every later packet,
  and a stream that falls a full frame behind rebases without sending a catch-up
  burst.
- Apply the same absolute scheduler to patch forwarding while preserving its
  initial frame delay and bounded backlog behavior.

## [0.5.0] - 2026-08-28

### Added

- Add Configuration Studio, a modeless graphical editor for the existing YAML
  codeplug. Its searchable hierarchy follows FNE system, zone, and channel
  ownership, with dense tables and field inspectors for systems, channels, web
  streams, groups, encryption keys, aliases, and referenced files.
- Add a slide-out zone layout editor that uses the same channel-card template,
  dimensions, colors, and two-dimensional canvas as the main console. Cards can
  be selected from the channel table and moved without changing the live
  operator workspace.
- Add draft undo and redo, field and cross-reference validation, external-change
  detection, atomic multi-file saves, restricted backups, and full or sanitized
  YAML exports. The validation drawer identifies the affected section, field
  path, and cause of every error or warning.
- Add per-codeplug operator settings for group membership, direction, source
  order, enabled state, and Studio layout identities. Group definitions remain
  in YAML, preserving compatibility with older DVM Console codeplug readers.
- Add a stop control for active TAR playback and local monitoring for generated
  tones, DTMF, pages, and alerts. The monitor copy is attenuated without
  changing transmitted PCM.

### Changed

- Present `mode: p25` as **P25 Phase 1** throughout Studio and omit the slot
  editor for P25 Phase 1 channels. DMR slots are displayed as whole numbers.
- Replace protocol-specific encryption numbers in Studio with named algorithm
  choices. Channel and local-key identifiers keep a fixed `0x` prefix, and the
  local-key editor derives the correct algorithm ID and required key length for
  P25 Phase 1, DMR, or NXDN.
- Route **View > Groups** to Studio's Groups page. Operational group controls
  remain immediate only while Studio is editing the active codeplug; definition
  and membership edits participate in review and save.
- Preserve unknown YAML mapping fields while their containing records remain in
  the draft. Edited sections use canonical formatting, and YAML constructs that
  cannot be rewritten safely open read-only.

### Fixed

- Prevent Configuration Studio menu commands, algorithm selection changes, and
  invalid draft states from terminating the desktop process. Keep the channel
  table independently scrollable when the layout drawer is open, and stop
  selection synchronization from repeatedly pulling overflow rows toward the
  drawer boundary.
- Keep draft identity and undo history stable across renames, duplicate names,
  reordering, deletion, and Save As. Preview settings and group state now follow
  the intended record instead of a display name alone.
- Ignore delayed FNE status and P25 KMM scheduling callbacks after session
  disposal, so session teardown cannot queue work against a closed console.
- Make generated-audio monitoring and TAR playback cancellation idempotent, and
  isolate monitor-output failures from the radio transmission.
- Reject malformed Opus, configuration, FNE, and recording inputs earlier;
  bound receive episode and stream bookkeeping; and keep recording scans and
  retention cleanup from following linked paths outside the configured root.

## [0.4.4] - 2026-08-27

### Changed

- Calibrate receive and transmit channel meters on the same -50 to 0 dBFS
  scale. The fill shows 50 ms RMS level with fast attack and controlled release,
  while a held peak marker changes from white to yellow at -12 dBFS and red at
  -6 dBFS.
- Make downsampling independent of input chunk boundaries at integer and
  non-integer sample-rate ratios. Keep anti-alias filtering at device and media
  boundaries, avoid default microphone-processing copies, and remove repeated
  front-shifting from the streaming resampler.
- Bound channel and patch transmit queues, report their measured depth, peak,
  capacity, and oldest-frame age through Engineering Health, and stop stale
  transmission if a queue reaches its safety limit.
- Keep TAR finalization jobs durable on disk when the bounded in-memory worker
  queue is full. Pending work resumes as capacity becomes available without
  growing an unbounded task backlog.
- Store TAR encryption as an explicit Unknown, Clear, or Secure state, together
  with the protocol algorithm and key identifiers when they are known. New
  recording metadata omits machine-specific catalog paths, while existing Opus
  recordings remain compatible.
- Debounce Debug Log text searches by 150 ms and limit the visible projection
  to the newest 5,000 matching rows. The existing retained-session limits and
  redacted export behavior remain unchanged.
- Separate console composition, restoration, event wiring, ownership, and
  teardown into explicit phases. Add application-owned formatting checks,
  dependency-boundary tests, and package size and file-count budgets to CI.

### Fixed

- Reset login retry pacing when an FNE login acknowledgement arrives. If
  authentication or configuration then makes no progress, close that system's
  session, wait one second, and retry with a phase-specific status message.
- Keep the meter's green, yellow, and red bands fixed across the full scale
  instead of compressing every color into any nonzero fill width.
- Play clear receive audio on encrypted digital channels in both fixed and
  selectable transmit modes. The **SECURE**/**CLEAR** selection now affects
  transmit only; encrypted receive audio still requires the matching key.
- Treat unreadable operator settings and inaccessible recording-catalog paths
  as recoverable conditions instead of failing startup or a catalog scan.
- Keep live TAR capture snapshots out of the finalization queue until their
  writers close. Finishing another recording can no longer delete an active
  PTT recording or crash Console when the operator releases PTT.
- Let explicit on-air encryption metadata correct an earlier DMR clear
  inference. A definitive call start after a confirmed terminator now creates a
  separate History and TAR episode even when the FNE reuses the stream ID, so
  adjacent calls cannot share audio or security metadata.
- Keep PTT release bounded without clipping normally queued speech. Console
  stops accepting microphone audio first, drains roughly one second of accepted
  audio at the normal cadence, and then sends the protocol completion and
  terminator.
- Remove the fixed 200 ms post-drain wait from the standard talk-permit cue, so
  microphone audio can begin as soon as the cue drains. Cold Bluetooth startup
  still uses measured presentation latency as its safeguard.
- Disable a channel's PTT control while that channel is actively receiving.
  A rejected toggle is not left latched, so the next press works normally once
  the receive call ends, and clicking the disabled PTT area does not toggle the
  channel's RX selection.
- Export Debug Logs through the file handle returned by the platform save
  picker. Exports no longer disappear when a provider does not expose a local
  path, and the status bar reports the destination or write failure.
- Roll back partially constructed sessions and dispose owned resources in a
  defined order without replacing the original startup or shutdown failure.

## [0.4.3] - 2026-08-27

### Changed

- Reduce package size through platform-specific audio and Avalonia backends,
  source-generated JSON metadata, partial managed linking, and removal of
  portable debug symbols.
- Stop idle audio-meter rendering until new samples arrive, back off stable
  default-device checks from one to five seconds, and reuse receive routing and
  decoder state instead of rebuilding common per-packet collections.
- Pool WAV decode buffers, encode selected Opus ranges directly, and finalize
  TAR recordings without rewriting the durable WAV before encoding the retained
  audio range.
- Send imported WAV and MPEG alert assets through the ordinary audio encoder in
  every mode. Keep generated tones, DTMF, QCII, and built-in alert sequences on
  their dedicated generation paths.
- Summarize jitter evidence per physical receive stream with first, periodic,
  and final reports. Pipeline diagnostics now separate intentional jitter hold,
  worker backlog, session-gate waiting, clear or encrypted processing, and mixer
  admission.
- Make per-packet transmit, PCM-level, and routine FNE keepalive logging opt-in
  through an **Enable verbose logging** checkbox under General settings. Batch
  retained log updates and add a 50,000-entry safety ceiling within the existing
  100 MB current-session limit.

### Fixed

- Pace generated and imported alert audio against a compensated 20 ms frame
  schedule so encode and send work does not accumulate into dropouts. Resume
  from a late frame without sending a catch-up burst, and retain a final partial
  analog frame without allocating a new buffer for every full frame.
- Reduce steady-state receive routing allocations by reusing mutation-time
  decoder snapshots and representing the common single-channel dispatch without
  a per-packet target array.
- Keep a P25 grant-demand TDU on the terminator lifecycle path because it only
  requests a peer grant. Begin an accepted call on the first LDU1 while keeping
  that packet on the 180 ms voice cadence and preserving LDU2 late entry.

## [0.4.2] - 2026-08-26

### Added

- Show the protocol, algorithm ID, and required key length beside an unavailable
  local DMR key on the Encryption Key Status page.

### Changed

- Carry the next-superframe DMR Association message indicator in the defined
  AMBE C3 late-entry fragments, and transmit burst-F single-burst algorithm/key
  metadata without inserting out-of-cadence privacy headers between voice
  superframes. Mark protected voice link control and privacy-indicator headers
  with DMR Association FID `0x10`, require that FID when decoding privacy
  metadata, and preserve the encrypted service option in the voice header,
  embedded link control, and terminator.
- Pace DMR, P25, and NXDN padded transmit tails before their terminators, and
  carry NXDN DES/AES `VCALL` and successor `VCALL_IV` messages in alternating
  four-frame SACCH superframes without stealing a voice slot. Send the initial
  `VCALL` and current IV together in the two FACCH halves, and derive each
  successor IV with the source-compatible 64-stage LFSR.
- Document that `dvmhost r05a06_dev` currently clears DMR burst-F single-burst
  data and replaces the second NXDN startup FACCH half with a copy of the first
  during network-to-RF regeneration. Console's local codecs retain those
  fields, but the affected late-entry paths are not end-to-end RF compatible.
- Flush pending operator settings before replacing the active session during
  codeplug loads, settings imports, profile changes, and settings resets.

### Fixed

- Start each DMR Association ARC4 privacy cycle after the required 256-byte
  RC4 discard, matching radio keystream alignment on receive and transmit.
- Decode selectable DMR, P25, and NXDN receive audio from each call's on-air
  encryption state. A channel in **CLEAR** can receive clear traffic without a
  configured secure key; **SECURE** remains secure-only and rejects clear calls.
- Distinguish DMR voice-LC, privacy, voice, embedded LC, single-burst, and
  reverse-channel signaling before decoding payload content. Support secure
  DMR late-entry decoding when the successor MI and burst-F key identity reach
  Console in a complete voice superframe. Keep call
  metadata out of voice duplicate/loss acceptance, and resolve clear late entry
  when burst F carries reverse-channel or other non-privacy signaling.
- Prevent valid NXDN voice payloads from being corrected into phantom FACCH
  call-control messages. Reassemble SACCH metadata for encrypted late entry,
  switch to an advertised successor IV only after its eighth voice frame, and
  preserve continuous 80 ms voice cadence across IV changes. Rebuild the EHR
  privacy processor when a new stream or loss boundary repeats its `VCALL`.
- Apply only the newest patch-source membership request when group edits and
  enable or disable operations overlap, so removed members do not reappear and
  edited patches work without restarting DVM Console. Isolate each source's
  decoder so an active call does not stall unrelated patch reconfiguration.
- Serialize shared FNE transport writes, make microphone-capture start, stop,
  fault, and disposal one owned lifecycle, and remove digital call-end paths
  that could bypass protocol tail cadence.
- Prepare replacement desktop sessions before changing window ownership and
  clean up the outgoing session without leaving mixed old/new references.

## [0.4.1] - 2026-08-25

### Added

- Add a documented `DvmConsole.CodeplugValidator` developer tool for validating
  codeplugs without starting the desktop application.
- Add regression coverage for audio-route rollback, asynchronous command faults,
  multi-stream receive completion, session-construction cleanup, and responsive
  toolbar breakpoints.

### Changed

- Collapse the flexible header spacer before toolbar contents, keep clocks
  immediately left of the operational controls, and move alert shortcuts into
  **MORE** before **TONES** and clocks. Account for interface scale and multiple
  enabled clocks when selecting an overflow tier.
- Wait for every physical stream in a logical receive episode before completing
  live playback and stopping its distinct TAR recording targets.
- Isolate runtime audio-setting changes, receive-episode completion, and
  construction rollback behind focused coordinators.
- Enforce explicit exception rethrowing and focused lifetime analyzers for the
  long-lived desktop service graph.
- Document the FNE, audio, and media probes as developer-only live validation
  harnesses rather than packaged applications.

### Fixed

- Apply microphone processing and device-route changes transactionally. Restore
  the previous route, input options, and Keep Mic Warm state when runtime
  reconfiguration fails instead of persisting a partial configuration.
- Contain and report asynchronous toolbar and settings command failures while
  reliably restoring command availability and treating cancellation as expected.
- Prevent receive playback and TAR teardown from overtaking buffered packets
  from another physical stream in the same logical receive episode.
- Restore viewport anchoring in the Activity sidebar and Console Settings Event
  History so incoming rows do not push the current reading position down when
  the operator has scrolled away from the top. Continue following new calls
  while already at the top.
- Release partially built sessions after main-window construction failures and
  start recording-finalization workers only after spool recovery completes.
- Close Opus, PCM prefix-stream, and serial PTT resources consistently when
  construction, decoding, or ownership transfer fails.

### Removed

- Remove unreachable standalone recording-catalog filtering, column-visibility,
  and code-behind handlers without changing Event History or recording behavior.
- Replace the ambiguous developer project name `DvmConsole.App` with the
  purpose-specific `DvmConsole.CodeplugValidator`.

## [0.4.0] - 2026-08-25

### Added

- Add searchable Settings navigation and responsive toolbar overflow.
- Add an optional, resizable **View > Engineering Health** pane for microphone
  freshness, receive queue pressure, transmit backlog, recording finalization,
  catalog work, route recovery, and latency measurements. It remains hidden by
  default and does not duplicate operational controls.
- Add release checksums, per-package SPDX SBOMs, and artifact attestations.

### Changed

- Advertise the version-derived `DVMC_NEO_<version>` software identifier to FNE
  systems instead of the previous `DVMC_AV_<version>` identifier.
- Move operational models and receive-lifecycle decisions into the Avalonia-free
  `DvmConsole.Operations` layer. `ConsoleSessionRuntime` now owns session-service
  lifetime while the existing view-model, audio, and vocoder facades remain
  compatibility boundaries.
- Observe each receive packet once through a precomputed route snapshot, replace
  counting wake permits with a coalesced signal, and time-budget presentation
  draining. Tracked routing-allocation cases for 1, 10, and 100 channels are at
  least 80 percent lower.
- Debounce settings persistence with latest-wins snapshots and explicit flush
  boundaries so slider changes do not perform filesystem work on the UI thread.
- Load and prune TAR metadata in one traversal and reconcile recording and
  history catalogs with keyed linear work.
- Prepare microphone capture before network activation and pace microphone and
  patch audio on the protocol's 20 ms clock so callback bursts cannot collapse
  call startup or overrun a destination.

### Fixed

- Restore the complete DMR call-start envelope, correctly identify DMR privacy
  headers, and sequence P25 grant demand, voice, and a single terminator so FNE
  systems receive valid call boundaries. Preserve the protected-service flag
  on encrypted P25 calls.
- Seed a nonzero FNE keepalive stream immediately after login so masters can
  track pings before any inbound voice traffic arrives.
- Resolve patch members by their configured channel identity, serialize each
  destination's lifecycle, and scope termination and loop suppression to the
  actual stream and outbound protocol. This restores cross-protocol and
  all-to-all patch audio, including an immediate reverse leg after a call ends.
  Patch target failures are recorded in Debug Logs.
- Keep receive control and metadata packets out of voice playout deadlines and
  missing-packet accounting. This release identified P25 grant-demand control
  separately from voice payloads but incorrectly classified it as metadata
  rather than a terminator.
- Recycle an individual FNE connection when its authorization or configuration
  handshake makes no progress, allowing a busy system to reconnect without
  restarting the application. Unanswered login requests now retain the normal
  first retry and then back off to a maximum 60-second interval; a successful
  login or explicit operator restart resets the cadence.
- Persist TAR finalization jobs before processing, resume valid jobs after a
  restart, retry transient failures, and quarantine invalid work without losing
  the source recording.
- Reject stale or faulted microphone capture before transmit readiness and
  recover capture generations deterministically.
- Release cold Bluetooth talk-permit gating on the first post-transition
  microphone callback, account for measured output presentation latency, and
  remove the pilot and cue tail. Faster wired and built-in routes retain their
  normal startup path, while operator audio remains blocked until the cue path
  is ready or fails safely.
- Detect two seconds of CoreAudio write no-progress using one shared watchdog
  without allocating partial-write buffers.
- Stop local Space PTT from consuming Space in editable fields or ordinary
  interactive controls while retaining configured local and OS-global PTT.

## [0.3.8] - 2026-08-23

### Changed

- Standardize macOS microphone capture on DVM Console processing and remove the Apple Voice Processing and high-quality AirPods controls. Normalize a saved Apple processing selection before audio startup.
- Avoid the additional Apple full-duplex route coordination during normal macOS PTT. Live Bluetooth-headset testing found lower transmit-start latency in DVM Console processing mode. Improvements throughout the audio chain mean most headsets should not require Keep Mic Warm, although exact timing remains device- and route-dependent.

### Fixed

- Drain each ended receive stream through its adaptive jitter worker before closing its audio and TAR recording state, preserving buffered transmission tails without coupling unrelated stream lifetimes.
- Pace TAR and web-stream PCM against a monotonic media clock so immediately accepting mixer lanes cannot be flooded by faster-than-real-time decoding and discard most of the audio.
- Retire a failed shared output mixer instead of allowing another client to reopen a lane on a permanently stopped physical output.
- Report receive and transmit vocoder levels over exact one-second PCM windows.
- Make channel-card mouse PTT honor toggle mode, with serialized held and latched call state that remains safe during slow audio startup and application shutdown.

## [0.3.7] - 2026-08-23

### Changed

- Make one-way patch direction explicit by selecting a source while treating every other member as a destination. Preserve existing settings by restoring the first saved member as the source.
- Compact the Groups page with collapsed member editors, responsive two-column patch cards, concise destination summaries, and overlap warnings sized to their text.
- Route patch-source traffic directly from FNE ingress through one adaptive jitter worker and one dedicated PCM decoder before re-encoding for each destination protocol. Keep patch forwarding independent from card Listen state, local playback volume, balance, output routing, and speaker mute.
- Decode only eligible patch sources: the selected source for one-way patches and all members for two-way patches. Multi-select members and one-way destinations no longer open unnecessary patch decoders.
- Allow an RX-only resource to source a one-way patch while requiring every destination, two-way member, and multi-select member to remain transmit-capable.
- Coordinate Apple Voice Processing I/O as one application-wide full-duplex route so RX, local cues, web streams, recording playback, and transmit capture share the same physical output and acoustic echo-cancellation reference. Switching back to DVM Console processing restores the ordinary CoreAudio route.

### Fixed

- Prevent Listen or TAR decoding from feeding the same PCM to a patch alongside the dedicated patch decoder, eliminating doubled, short-repeat, and jittery forwarded audio.
- Deliver patch traffic to its adaptive jitter queue before UI presentation so a busy settings or presentation thread cannot key a destination and then starve its audio.
- Release failed outbound patch sessions from router state so the next source audio block can establish a fresh destination call instead of leaving the patch keyed but silent.
- Rebuild an active one-way route when only its selected source changes, and avoid issuing duplicate destination-start requests when decoded audio arrives before an explicit call-start observation.
- End active destination sessions when a patch is disabled and suppress rewritten or delayed FNE echoes through teardown so an overlapping member cannot cascade that audio into another patch. Preserve isolation reference counts when multiple active patches target the same member.
- Prevent Apple Voice Processing and ordinary CoreAudio outputs from competing for the same device, including while the microphone is kept warm. Fail stalled voice-output writes promptly, carry physical callback and starvation health through shared mixer lanes, restart failed receive routes without waiting for another traffic frame, and observe mixer failures without unobserved-task crash records.
- Bound application shutdown so a CoreAudio teardown that does not return cannot leave DVM Console open indefinitely after Quit.

## [0.3.6] - 2026-08-22

### Added

- Add an opt-in Windows communications microphone mode that requests endpoint-provided echo cancellation, noise suppression, and automatic gain control while bypassing DVM Console microphone processing. Available effects remain dependent on Windows, the selected driver, and the endpoint.

### Changed

- Replace the legacy Windows WinMM audio backend with NAudio 3 shared, event-driven WASAPI capture and playback. Preserve stable fixed routes, Multimedia-role system-default following, existing queue and starvation diagnostics, and deferred default-microphone migration during PTT.

### Fixed

- Keep completed TAR recordings playable and searchable in History when their live session rows age past the in-memory call limit, without requiring an application restart to reload them from disk.

## [0.3.5] - 2026-08-22

### Changed

- Reduce managed allocation in receive routing, vocoder frame assembly, native vocoder calls, and DMR/P25/NXDN transmit packetization without changing output behavior.
- Refactor settings, desktop sessions, FNE, audio, playback, and native-resource ownership into focused internal components while preserving existing public and compatibility contracts.
- Strengthen automated compatibility, analyzer, native-audio, and packaged-application checks.

### Fixed

- Keep Activity, PTT, modeless tools, and other session-owned state attached to the replacement model after a codeplug reload.
- Await and serialize PTT, background work, shared capture, mixer recovery, and desktop-session shutdown so retired resources cannot remain active or be revived.
- Marshal bound web-stream state through Avalonia and observe intentional background-operation failures.

## [0.3.4] - 2026-08-21

### Fixed

- Record inbound calls whenever TAR is armed for a resource, even when its live RX card is not selected. Keep recording ownership, encryption metadata, and call finalization aligned when another zone copy owns the shared receive decoder.
- Complete the local talk-permit tone before processing a later stop edge from global, active-system, or serial PTT so toggle-mode keybinds retain the same audible transmit-ready indication as press-and-hold operation.

## [0.3.3] - 2026-08-21

### Added

- Add per-FNE, packet-aligned RX jitter buffers with fixed or default-on adaptive modes for P25, DMR, and NXDN so packets that arrive out of order can be restored before their playout deadline in both live listening and patch-source decoding. Adaptive targets learn transport variation from zero through nine protocol frames per connection and protocol while every receive stream retains an independent, call-stable playout clock.
- Add toolbar output-mute controls for all RX, the selected system, and the selected zone beside Warm mic. Each suppresses only its live speaker scope without interrupting decode, call state, patching, or TAR recording.

### Changed

- Decode complete RX network packets into caller-owned PCM batches before proceeding through the chain, reducing allocation and scheduler pressure without changing per-mode gain, smoothing, or optional processing.
- Shorten receive cleanup to one second of inactivity plus one second of grace, with a two-second post-terminator hold. Report packets restored to playout order separately from packets that miss the jitter deadline.
- Show Activity Event History for RX-enabled channels by default, with independent `Active`/`All` and `Zone Wide`/`System Wide` filters that do not trigger the History window when double-clicked.
- Restore the main console's last normal size and position at launch when that position remains reachable on a connected display.
- Replace rapidly changing per-packet connection details with readable connection-session RX/TX totals, bounded current-stream summaries, and coalesced health updates. Keep the current Debug Log session within a 100 MB memory limit while discarding the oldest entries first.
- Keep responsive per-FNE jitter controls beside a state-aware Connect/Disconnect action, apply selection changes immediately, and show the learned adaptive target and reorder/deadline effectiveness counters for each connection. Open Encryption Key Status directly at the channel key-status section.

### Fixed

- Age an overflowing live RX lane at whole-packet boundaries so newly arrived speech replaces stale speaker-bound audio while TAR keeps the complete decoded timeline. Detect a stalled physical output callback on macOS and Windows, expose pending physical starvation and per-lane high-water evidence, and separate UDP arrival, FNE handling, decoder queue, mixer, and device timing in diagnostics.
- Keep three complete P25 LDUs of bounded live-lane headroom so ordinary packet bursts do not age current speech, and make RX meters follow the selected receive stream immediately while UI lifecycle work catches up.
- Finalize TAR Ogg Opus recordings with an exact PCM-duration end timestamp so players do not advertise codec-frame padding after the recording's real audio ends.
- Interpret unprefixed P25 key IDs as hexadecimal, matching legacy WPF codeplugs so FNE/KMM requests use the intended key ID.
- Match the legacy console's post-connect settling delay and per-key request pacing so FNE/KMM servers can service every configured P25 key request.
- Refresh selectable-encryption controls when a delayed FNE/KMM key arrives so a restored secure channel presents its `SECURE` state as soon as the key becomes available.
- Reflow FNE identity, jitter controls, connection actions, microphone processing values, and AGC controls instead of clipping them when Console Settings is narrowed.
- Keep Debug Logs responsive during busy traffic with an incrementally maintained virtualized view, compact rows, stable reading position as new entries arrive, safe row recycling, and a current-session-only 100 MB memory limit that discards the oldest entries first.
- Preserve saved Alert/QCII tone-pattern steps shorter than 0.25 seconds instead of raising them to the former preset floor.

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

[Unreleased]: https://github.com/RdWing/dvmconsole/compare/v0.5.5...HEAD
[0.5.5]: https://github.com/RdWing/dvmconsole/compare/v0.5.4...v0.5.5
[0.5.4]: https://github.com/RdWing/dvmconsole/compare/v0.5.3...v0.5.4
[0.5.3]: https://github.com/RdWing/dvmconsole/compare/v0.5.2...v0.5.3
[0.5.2]: https://github.com/RdWing/dvmconsole/compare/v0.5.1...v0.5.2
[0.5.1]: https://github.com/RdWing/dvmconsole/compare/v0.5.0...v0.5.1
[0.5.0]: https://github.com/RdWing/dvmconsole/compare/v0.4.4...v0.5.0
[0.4.4]: https://github.com/RdWing/dvmconsole/compare/v0.4.3...v0.4.4
[0.4.3]: https://github.com/RdWing/dvmconsole/compare/v0.4.2...v0.4.3
[0.4.2]: https://github.com/RdWing/dvmconsole/compare/v0.4.1...v0.4.2
[0.4.1]: https://github.com/RdWing/dvmconsole/compare/v0.4.0...v0.4.1
[0.4.0]: https://github.com/RdWing/dvmconsole/compare/v0.3.8...v0.4.0
[0.3.8]: https://github.com/RdWing/dvmconsole/compare/v0.3.7...v0.3.8
[0.3.7]: https://github.com/RdWing/dvmconsole/compare/v0.3.6...v0.3.7
[0.3.6]: https://github.com/RdWing/dvmconsole/compare/v0.3.5...v0.3.6
[0.3.5]: https://github.com/RdWing/dvmconsole/compare/v0.3.4...v0.3.5
[0.3.4]: https://github.com/RdWing/dvmconsole/compare/v0.3.3...v0.3.4
[0.3.3]: https://github.com/RdWing/dvmconsole/compare/v0.3.2...v0.3.3
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
