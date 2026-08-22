# Settings Reference

This page summarizes user-facing settings in the console.

---

# Settings Menu

## Toggle Push To Talk Mode

When enabled, clicking PTT toggles transmit on or off.

When disabled, PTT behaves as press-and-hold.

Default:

```
Off
```

This setting is saved.

## Talk Permit Tone

When enabled, the console plays a short local tone when transmit begins.

This is local operator feedback only; it is not transmitted. Global,
active-system, and serial PTT wait for the cue to complete before microphone
audio is released, including when the shared PTT setting uses toggle mode.

## Connection Chimes

Connection chimes are enabled by default. Disable this setting if the operator
does not want local audible feedback when an FNE connects or disconnects.

## Interface Size

Open **Settings > All console settings** and use the Appearance controls to
adjust the console display.

- **Text size** changes the inherited application font size.
- **Interface scale** scales the complete main console and Console Settings UI.

Both settings apply immediately and are saved for the next launch.

## Mute RX Audio While Transmitting

When enabled, local RX speaker playback is muted while the console is transmitting.

This does not block received network traffic, logs, patch forwarding, or RX visual state. It only suppresses local speaker playback during TX.

Use this when operators use speakers and you want to reduce the chance of received audio feeding back into the microphone.

## Retain Patch State on Startup

When enabled, patch active/on-off state is restored on startup.

Patch members always persist. This setting only controls whether enabled patches come back enabled after restart.

Default:

```
Off
```

## Restore Selected Channels On Startup

When enabled, selected resources are restored on startup.

Configured encrypted P25 resources request keys through KMM whenever the relevant FNE connects, whether or not this setting is enabled. Restored selection state controls only which resources return selected; the local key file remains the automatic fallback.

When disabled, selected resources and per-resource volume do not come back sticky on startup.

## Toolbar Clocks

Open **Settings > Clock settings** to show the General page in Console Settings. Up to eight toolbar clocks can be enabled.

Each clock has:

- an enable/disable checkbox
- a UTC offset, such as `UTC+00`, `UTC-05`, or `UTC+09`
- a preset box color for visual grouping

The General page also controls the shared clock display format:

- `Use 24-hour time`
- `Show seconds`

Clock settings are saved and restored on startup. Enabled clock slots, UTC offsets, colors, 12/24-hour mode, and seconds display are all sticky user preferences.

## Audio Settings

Opens Console Settings on the Audio page.

See **Audio Settings** for details.

The main toolbar has three independent live-output mute controls:

- `S` mutes or restores the currently selected FNE system.
- `Z` mutes or restores the currently selected zone.
- the unprefixed speaker button mutes or restores all live RX output.

These controls affect speaker playback only. Receive decoding, call lifecycle,
patching, and TAR recording continue. System and zone scopes compose: restoring
one scope does not override another scope that is still muted.

Microphone gain, EQ, AGC, warm-microphone, and Apply controls wrap onto
additional rows when Console Settings is narrowed so every control remains
reachable.

## Import / Export Settings

Opens the Settings Transfer window.

Use this to move console preferences between machines without manually copying `UserSettings.json`.

The transfer file is a portable JSON file. You can choose which categories to export or import, including:

- console layout and widget positions
- audio routing and volumes
- TAR configuration
- patch and multi-select group state
- custom alert tones
- toolbar clocks
- startup restore state
- operator preferences
- call history window preferences
- keybinds and selectable encryption state

Press `Ctrl+A` in the transfer window to select all categories.

On import, only the selected categories are replaced. The console reloads the current codeplug/widgets after import so layout and routing changes take effect immediately.

## Reset Settings

Clears saved user settings.

Use with care. This can remove saved window layout, widget positions, audio routing, selected channel state, and other preferences.

## Widgets

The General page can show or hide system status cards, channel widgets, and toolbar alert buttons. **Lock channel widget positions** prevents card dragging; clear it to arrange cards on the active tab.

## Keyboard PTT Keys

Choose Space or F1 through F19 for global PTT and active-system PTT under **Channels** or **Console Settings > PTT**. Global PTT keys every `TX`-selected resource; active-system PTT limits those resources to the active system tab. The keys must be unique and share the same press-and-hold or toggle setting.

The serial hardware PTT can independently operate all `TX`-selected resources or only those in the active system. Apply the serial settings after changing its scope.

---

# View Menu

## Select User Background

Chooses a custom background image for the main console.

## Dark Mode

Toggles the app theme.

## Lock Widgets

Prevents resource and status widgets from being moved.

## Reset Widget Layout

Snaps channel cards back to a grid-style layout.

## Event History

The Event History can be docked in the collapsible Activity sidebar or opened as a separate window. **Snap detached Event History** keeps the detached window aligned with the main console.

## Groups

Opens Console Settings on the Groups page.

## Keep Window on Top

Keeps the console above other windows.

---

# Tools Menu

## Talkgroup Audio Recorder

Open from:

```
Tools > Talkgroup Audio Recorder
```

Sub-items:

- Viewer
- Configuration

See **Configurations > Talkgroup Audio Recorder** for TAR recording, retention, playback, filtering, and retention details.

## FNE Connection Manager

Opens the **Connections** page for manual connection controls, the current or
most recently completed RX stream, local receive-health counters, and key status
for configured FNE systems. The view is refreshed at a stable cadence rather
than for every packet.

### Per-connection RX network jitter buffer

Each FNE connection has independent P25, DMR, and NXDN jitter settings. Choose
**Off**, a fixed packet-aligned delay, or **Adaptive** from the compact selectors
beside that connection's state-aware Connect/Disconnect and Restart controls. The buffer
holds complete network packets before decoding. If a packet arrives out of order
but before its playout deadline, the console restores it to the correct place in
the stream. If the deadline expires first, playback continues and the decoder
applies its normal loss concealment; one missing packet cannot stall the call.

Adaptive mode measures transport arrival variation once per FNE connection and
protocol. Each new receive stream snapshots the current target for its complete
call, so simultaneous streams keep independent sequencing and one call never
changes another call's playout clock. The target rises immediately in whole
packet steps when late arrival evidence requires more headroom. It falls one
packet only after three clean completed calls. Missing packets that never arrive
do not by themselves increase the target. Adaptive is the default for new or
previously unsaved settings. It starts at zero added frames on a clean
connection and can learn up to nine frames.

Only protocol-aligned durations are offered:

- P25: zero through nine 180 ms LDUs; adaptive range 0–1620 ms.
- DMR: zero through nine 60 ms packets; adaptive range 0–540 ms.
- NXDN: zero through nine 80 ms packets; adaptive range 0–720 ms.

The fixed or currently learned duration is added to the existing decode and
speaker-output path. In normal conditions, estimated packet-arrival-to-speaker
latency is the stream's jitter target plus approximately 80–110 ms. The physical
audio device can add a small route-dependent amount that the application cannot
measure exactly. Turning the jitter buffer off minimizes latency but removes the
packet reordering opportunity.

Changing a selection saves that FNE's settings immediately and safely recreates
active listening and patch-source decode sessions.

Below the selectors, **Adaptive learned** shows the current target for every
protocol using adaptive mode. **Jitter effectiveness** shows how many delayed
packets were restored before playout and how many expected packets missed their
deadline. These values refresh at the same stable cadence as the connection
diagnostics rather than on every packet.

The FNE identity, jitter selectors, and connection actions wrap onto additional
rows when the window is narrow. One state-aware button alternates between
**Connect** and **Disconnect**; **Restart** remains separate.

Debug Logs report a packet successfully restored to playout order as a jitter
buffer reorder event. A separate warning reports when an expected packet misses
its deadline and playback advances. Pipeline timing identifies whether the
stream used a fixed delay or an adaptive target. The `jitter/decoder queue`
value is one component of the enclosing `total FNE-to-mixer` time, so the two
values can be nearly identical when decoding and mixer admission take only a few
milliseconds.

Stream-scoped timing high-water marks reset for the next stream. The
per-connection effectiveness totals retain completed-call evidence until the
FNE disconnects or its jitter setting changes. Already-emitted reorder,
deadline, and timing messages remain in the Debug Logs session within the
displayed 100 MB in-memory limit. Entries are retained only for the current
application session. When the limit is reached, the window discards the oldest
entries first and reports the discarded count.

## Encryption Key Status

Opens Console Settings directly at the channel key-status section of the
**Connections** page. The view displays key identifiers and availability only;
key material is never displayed or logged.

Selectable-encryption state is restored independently of key arrival. When a
restored channel is secure and its configured key is available only from KMM,
the channel remains unavailable during the post-connect request interval. As
soon as the matching key arrives, its encryption capability refreshes and the
channel presents the restored **SECURE** control. The key itself is not persisted
or displayed.

---

# Help Menu

## Documentation

Opens the searchable documentation viewer. Pages are read live from the
release-branch documentation tree on GitHub and rendered with headings, lists,
tables, links, and code blocks. An internet connection is required; Markdown is
not bundled into the application package.

## About

Shows the current application version and seven-character commit ID, copyright, AGPL license notice, project attribution, and repository links.
