# Channel Controls, Receive Streams, and History Catalog Design

Date: 2026-08-17
Status: Approved

## Scope

This change addresses the reported channel-card input and visual-state regressions, adds bulk receive and global-PTT choices, exposes live receive activity on system/zone tabs, stabilizes concurrent receive and microphone/audio lifecycle handling, corrects built-in alert tones, makes recording finalization and playback reliable under load, consolidates completed recordings into Event History, and fixes transient-dialog and recent-path layout. BER reporting is explicitly out of scope.

## Channel-card interaction

The channel card will toggle receive only when the pointer gesture originates from non-interactive card space. Pointer gestures originating from a button, slider, or any visual descendant of an interactive control (including a `PathIcon`, `TextBlock`, content presenter, or control template element) will not arm the card gesture or capture the pointer for card toggling.

TX, PAGE, ALERT, and TAR will use a single state-driven visual implementation. Enabled state is the source of truth for the button background and border. Hover and pressed states may adjust affordance without replacing the enabled color. Code-behind pointer handlers will not assign competing local brush values. A disabled button that is clicked to enable must display its enabled color immediately while the pointer remains over it, and moving across button text/icon/chrome must not flicker.

Tests will exercise pointer input through nested button content as well as the button surface, and will verify enabled, disabled, hovered, and clicked visual states.

## Channels menu

The existing global disable action remains. Add these actions and labels:

- Enable all receive: enable every configured channel in every system.
- Enable all receive (zone): enable channels in the selected system's current zone.
- Disable all receive (zone): disable channels in the selected system's current zone.

The zone actions will be unavailable when there is no selected system or zone. Each action will update the same receive state used by individual cards and will preserve unrelated channel selections such as TX, PAGE, ALERT, and TAR.

## Global PTT binding

The operator can explicitly configure or clear the global keyboard PTT binding. The portable preset set includes Space and F1 through F19. Selecting None disables keyboard global PTT without disabling the on-screen or optional serial PTT sources. Changing or clearing the binding safely releases any active keyboard-driven PTT before replacing its input source, persists the selection, and updates both the main-window menu and Console Settings.

Focused-window and OS-global capture will use the same mapping on Windows and macOS. Unsupported or malformed persisted values normalize to None rather than unexpectedly assigning Space. The UI will describe the configured key, or show Keyboard PTT disabled when no key is assigned.

## System tab status accents

The system tab header will render the system name and status glyph as separate elements. Each configured system receives a deterministic accent from an accessible palette in codeplug order, so its status dot remains visually distinct from the other systems across launches. The existing filled-versus-hollow glyph continues to communicate connected versus disconnected state; detailed transitional and fault status remains available from the system status card and tooltip.

System and zone tab headers will also include a narrow activity bar below the label. The system bar becomes active whenever any channel in that system is receiving; each zone bar becomes active whenever any channel in that zone is receiving. Activity must update for tabs that are not selected so simultaneous calls remain visible. Selection continues to use the normal tab selection treatment, connection continues to use the status glyph, and receive activity uses the bar, avoiding one color or marker carrying multiple meanings. The activity state changes immediately at call start and end without animation or flashing that could be mistaken for an alert.

## macOS application identity

The Avalonia `Application.Name` will be `DVM Console`, matching `CFBundleName`, `CFBundleDisplayName`, and the main-window title. The macOS application menu must show `DVM Console` for both unbundled development launches and packaged `.app` launches; it must not fall back to `Avalonia Application`.

## Alert-tone fidelity

Alert 1, 2, and 3 will match the established vocoder-aligned signaling patterns: Alert 1 is continuous 1000 Hz for 3 seconds; Alert 2 alternates 1500 Hz and 800 Hz in 240 ms steps for seven cycles; Alert 3 uses eight 240 ms bursts of 1000 Hz separated by 240 ms silence. The 240 ms segments are exact multiples of the 20 ms vocoder frame window and end on whole tone cycles, preventing boundary artifacts. Generated peak level will target -25 dBFS. No call site may override that calibrated level with a louder amplitude.

Tone segments will preserve clean boundaries so the generator does not introduce clicks between frequency or silence transitions. Tests will verify 1000 Hz Alert 1/3 output, frequency, vocoder-frame alignment, timing, sample length, peak level, and segment-boundary continuity against the established patterns.

## Receive health and warm-microphone transitions

Desired receive selection is separate from the health of the current decoder and playback session. A recoverable shared-output-route failure must not clear the operator's RX selections. When traffic is still arriving while the FNE remains connected, the application will reconcile selected channels with active receive sessions and recover the shared route for all affected channels with bounded retry and visible diagnostics. A persistent per-channel decode fault remains isolated to that channel.

Changing Keep transmit microphone warm is a serialized desired-state transition. Disabling it releases only the warm capture lease; it cannot stop capture while a transmission lease is active, stop an active receive endpoint, or tear down a shared macOS voice-processing session still used by playback. Rapid setting changes cannot allow an older asynchronous request to overwrite the newest selection.

Tests will simulate a shared output-route interruption across multiple selected channels, continued incoming traffic, disabling warm capture during a transmission, and stopping capture while receive playback remains active.

## Dialog and recent-codeplug layout

Transient message, confirmation, and text-entry windows will size to their content within practical minimum and maximum bounds. Long messages will wrap and become internally scrollable rather than stretching the window beyond the work area. The Unable to open codeplug dialog will no longer inherit an excessive fixed vertical minimum. The shared dialog construction path will be used for equivalent ad hoc windows so their sizing behavior remains consistent.

Open Recent entries will render as bounded, path-aware menu content: the filename remains prominent, the parent path is middle-elided when necessary, and the full unmodified path is available in a tooltip. The exact full path remains the menu item's command value, so display shortening cannot change which codeplug is opened. Entries must remain usable without horizontal clipping on macOS and other supported desktop platforms.

## Receive stream and call lifecycle

Receive lifecycle state will be keyed by system, protocol, destination/channel, and stream ID rather than inferred from mutable channel state alone. Each channel has at most one current receive owner, while recently ended streams retain a short-lived terminal record.

An explicit protocol terminator hard-closes its stream. Subsequent voice packets for that stream are treated as late traffic and cannot reopen the channel, create another History row, or create another recording. An inactivity timeout soft-closes the stream only after a short grace interval; packets for the same stream during that interval resume the existing logical call, History entry, and recording. A valid new stream supersedes the old stream deterministically and prevents interleaved packets from flipping channel ownership.

Sequence diagnostics will publish when the late, duplicate, or missing counters change. A previously observed cumulative issue will not be re-reported on every later frame. Concurrent streams on different channels remain independently queued and decoded.

Decoded-audio callbacks will include the originating stream ID. Recording attribution and patch forwarding will use that immutable identity rather than reading `ChannelViewModel.StreamId` after queued work has advanced.

## Recording lifecycle and playable-content validation

Active receive recordings will be keyed by channel and stream. Finalization, silence analysis/trimming, Opus encoding, metadata writing, and catalog indexing will run outside the per-channel receive worker so that network audio processing cannot block on filesystem or encoder work.

A completed catalog item is playable only after its audio file has been finalized and validated. Validation will reject missing, empty, undecodable, or zero-duration output. Recordings whose silence trimming removes all usable content will retain a diagnostic outcome but will not expose a misleading Play button. Tests will encode and decode representative non-silent samples and verify that recorded duration and audible sample content survive finalization.

Playback will have one stable coordinator state. Incoming calls and catalog changes will not recreate the active row or play control, restart playback, or block audio delivery. Repeated Play/Stop input will be serialized, and UI state changes will be dispatched without competing with receive-frame processing.

## Recorder tab

Recorder becomes configuration-only. It contains:

- recording storage location;
- retention and pruning controls;
- TAR setup;
- per-channel recording enablement;
- ignored subscriber/RID configuration.

Completed recordings, recording search, catalog filters, visible-field selection, and recording file actions move out of Recorder. System channel lists will be grouped in collapsible sections so busy codeplugs do not produce an unnecessarily long initial page.

## Unified Event History catalog

History is the single browsing and playback surface for live calls, console transmissions, operational events, and completed recordings. It will use a unified presentation model rather than adding a second recording row for a call already in memory.

When a recording completes during the current session, its metadata attaches to the matching History call by direction, system, protocol, stream, and start-time proximity. Recordings loaded from previous sessions synthesize completed call rows keyed by stable recording ID. The unified catalog de-duplicates a synthesized recording row when a matching live History entry exists.

The default History row remains compact and shows:

- date/time;
- channel and system;
- subscriber ID and alias to talkgroup;
- direction and protocol;
- encryption state;
- duration;
- Play when validated audio is available.

Recording filename, audio diagnostics, format/size, Open, Stop, and Delete are placed in expandable details or an overflow action area rather than permanent wide columns.

The History header provides a primary search field. Advanced filters are collapsed by default and include direction, protocol, encryption, system, channel, talkgroup, subscriber ID, alias, and start/end date. Active filters remain visible through a concise summary and a single clear action. Existing export and clear-history actions remain available. Search includes both History and attached recording metadata.

The compact Activity sidebar continues to show recent activity and a Play affordance, but it does not render the full catalog details. Catalog indexing and recording completion update existing view models incrementally rather than clearing and repopulating observable collections. A background scan is used for initial/manual catalog discovery, with small UI-thread updates. This prevents busy-system catalog churn from causing jerky playback or graphical races in sidebar Play buttons.

Delete removes the recording and its sidecar after confirmation. The History call remains as an unrecorded event when it originated in the current session; a recording-only synthesized row is removed with its recording. Open and Play are unavailable for invalid or missing files.

## Validation

Automated tests will cover:

- nested button-content input never toggling the parent channel card;
- immediate, stable button colors across enabled, hover, pressed, and disabled transitions;
- global and selected-zone receive actions;
- optional global PTT binding with portable Space and F1-F19 presets;
- stable, distinct system-tab status accents with connected/disconnected glyphs;
- system and zone activity bars tracking receive state independently of tab selection;
- `DVM Console` as the macOS application-menu name in development and packaged launches;
- exact alert patterns and a -25 dBFS peak target without call-site gain overrides;
- route-wide RX recovery without losing desired channel selections;
- disabling warm microphone capture without interrupting active TX or RX;
- content-sized bounded dialogs and path-aware Open Recent entries that preserve full paths;
- independent concurrent streams and deterministic same-channel ownership;
- duplicate, late, timeout-resume, terminator, and superseding-stream sequences;
- no duplicate History entries or split recordings for a resumed logical call;
- correct stream attribution for queued decoded samples;
- non-blocking recording finalization and incremental catalog updates;
- playable, non-silent finalized recordings and rejection of invalid output;
- stable playback controls while simulated traffic and recording completions arrive;
- unified History search, advanced filters, older recording synthesis, de-duplication, and delete behavior.

Build and focused test suites will run before the broader solution tests. Manual operator validation will exercise channel-card hit targets, hover transitions, a busy multi-stream traffic scenario, sidebar playback during incoming calls, and the reorganized Recorder and History tabs.
