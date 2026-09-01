# Settings reference

This page describes the settings available to operators.

---

# Settings menu

## Toggle push-to-talk mode

When enabled, each PTT click starts or stops transmit. When disabled, PTT uses
press-and-hold behavior.

Default:

```
Off
```

DVM Console saves this setting.

## Talk permit tone

When enabled, DVM Console plays a short local tone when transmit begins.

This is local operator feedback only; it is not transmitted. Global,
active-system, and serial PTT wait for the cue to complete before microphone
audio is released, including when the shared PTT setting uses toggle mode.

## Connection chimes

Connection chimes are enabled by default. Turn them off to silence local
feedback when an FNE connects or disconnects.

This option is on the General page under **Settings > All console settings**.

## Verbose logging

**Enable verbose logging** is on the General page under **Settings > All
console settings**. It adds per-packet, PCM-level, and routine keepalive details
to Debug Logs. Leave it off unless those details are needed because it can use
more CPU and memory on a busy system.

## Monitor generated tones locally

Generated-tone monitoring is enabled by default. Turn it off to transmit alert
tones, DTMF, generated-tone presets, and QCII pages without playing the local
monitor copy through the selected speaker route.

This setting affects only local monitoring. It does not change transmitted
audio.

This option is on the General page under **Settings > All console settings**.

## Interface size

Open **Settings > All console settings** and use the Appearance controls to
adjust the main console and Console Settings display.

- **Text size** changes the inherited application font size.
- **Interface scale** scales the main console and Console Settings UI.

These settings apply immediately. The optional Engineering Health rail stores
its visibility and height separately and does not move or rewrite channel
cards.

## Mute RX audio while transmitting

When enabled, DVM Console mutes local RX speaker playback during transmit.

Network traffic, logs, patch forwarding, and RX visual state continue. Only the
local speaker output is muted.

Use this with speakers to reduce the chance that received audio feeds back into
the microphone.

## Retain patch state on startup

When enabled, DVM Console restores each patch's active state at startup.

Patch membership always persists. This setting controls only whether active
patches return active after a restart.

Default:

```
Off
```

## Restore selected channels on startup

When enabled, DVM Console restores selected resources at startup.

Configured encrypted P25 resources request keys through KMM whenever their FNE
connects, regardless of this setting. Restored selection controls only which
resources return selected. The local key file remains the automatic fallback.

When disabled, resources start unselected and their saved volumes are not
restored.

## Toolbar clocks

Open **Settings > Clock settings** to show the General page in Console Settings.
You can enable up to eight toolbar clocks.

Each clock has:

- an enable/disable checkbox
- a UTC offset, such as `UTC+00`, `UTC-05`, or `UTC+09`
- a preset box color for visual grouping

The General page also controls the shared clock display format:

- `Use 24-hour time`
- `Show seconds`

DVM Console restores the enabled slots, UTC offsets, colors, 12/24-hour format,
and seconds display at startup.

Enabled clocks sit directly to the left of **Keep Mic Warm** and the output-mute
controls. As the window narrows, the flexible space between **Help** and this
group collapses first. Alert shortcuts then move into **MORE**, followed by
**TONES** and the clocks. The transition accounts for interface scale and the
number of enabled clocks.

## Audio settings

This opens the Audio page in Console Settings.

See **Audio Settings** for details.

The main toolbar has three independent live-output mute controls:

- `S` mutes or restores the currently selected FNE system.
- `Z` mutes or restores the currently selected zone.
- the unprefixed speaker button mutes or restores all live RX output.

These controls affect speaker playback only. Receive decoding, call lifecycle,
patching, and TAR recording continue. System and zone scopes compose: restoring
one scope does not override another scope that is still muted.

When Console Settings is narrow, the microphone gain, EQ, AGC, warm-microphone,
and Apply controls wrap onto more rows so each one remains reachable.

DVM Console applies microphone processing and device-route changes as one
transaction. If reconfiguration fails, it restores the previous input, output,
processing options, and warm-microphone state. The failed settings are not
saved, and Audio status and Debug Logs report the problem.

## Import and export settings

Use **File > Export Settings…** to save the current console settings as a
JSON profile. The file includes layout, audio routing, TAR settings, group
operator state, custom alert settings, clocks, startup preferences, history
preferences, PTT keybinds, and selectable-encryption state. It does not include
the Configuration Library, recordings, or managed alert audio files. Reimport
custom alert audio after moving settings to another computer.

Use **File > Import Settings…** to apply a complete exported profile. DVM
Console reloads the current managed configuration so imported layout and
routing changes take effect. The import does not switch to a different managed
configuration.

## Named settings profiles

**File > Settings Profiles** stores named profiles on the current computer.
**Save current profile…** captures the current settings. **Load profile** shows
a summary and restores the saved operator settings after confirmation. It keeps
the active managed configuration and current channel selection. **Delete
profile** removes a saved profile after confirmation.

## Reset settings

This clears saved user settings.

It can remove the saved window layout, widget positions, audio routes, selected
channel state, and other preferences.

## Widgets

The General page can show or hide system status cards, channel widgets, and
toolbar alert buttons. **Lock channel widget positions** prevents dragging.
Clear it before arranging cards on the active tab.

## Keyboard PTT keys

Choose Space or F1 through F19 for global and active-system PTT under
**Channels** or **Console Settings > PTT**. Global PTT keys every `TX`-selected
resource. Active-system PTT limits the selection to the active system tab. The
two bindings must use different keys and share the press-and-hold or toggle
setting.

Serial hardware PTT can operate every `TX`-selected resource or only those in
the active system. Apply the serial settings after changing this scope.

---

# View menu

## Select User Background

This selects a custom background image for the main console.

## Dark Mode

This switches the application theme.

## Lock Widgets

This prevents resource and status widgets from moving.

## Reset Widget Layout

This returns channel cards to a grid layout.

## Event History

Recent calls appear in the collapsible Activity sidebar. Open the complete
history from **View > History**, the Activity heading, or **Tools > Talkgroup
Audio Recorder > Viewer**. Each route opens the History page in Console
Settings.

When either list is scrolled away from the top, incoming calls do not move the
visible row. A list already at the top continues to follow new calls.

The complete Event History retains the newest 5,000 calls and events from the
current application session, including entries without TAR recordings. These
non-recorded entries are cleared when the application exits. TAR recording
catalog rows remain available according to the configured recording retention
policy. To keep the main console responsive, its compact Activity sidebar shows
only the newest 100 entries matching its current filters.

## Groups

Opens Configuration Studio on the Groups page. Group definitions stay in the
managed YAML revision. Membership, direction, and enabled state are saved as
operator state for that managed configuration.

## Keep Window on Top

This keeps the console above other windows.

---

# Tools menu

## Talkgroup Audio Recorder

Open from:

```
Tools > Talkgroup Audio Recorder
```

Sub-items:

- Viewer
- Configuration

See **Configurations > Talkgroup Audio Recorder** for recording, playback,
filtering, and retention details.

## FNE Connection Manager

This opens the **Connections** page. It has manual connection controls, the
current or most recently completed RX stream, local receive-health counters,
and key status for configured FNE systems. The view refreshes at a steady
cadence instead of once per packet.

If an FNE does not answer login requests, DVM Console uses the normal first
retry, then waits 10, 20, 40, and 60 seconds between attempts. The interval is
capped at 60 seconds and resets after a successful connection or operator
restart. If authorization or configuration starts but stops progressing, DVM
Console recycles only that FNE's session.

### Per-connection RX network jitter buffer

Each FNE connection has separate P25, DMR, and NXDN jitter settings. Choose
**Off**, a fixed packet-aligned delay, or **Adaptive** beside that connection's
Connect/Disconnect and Restart controls. The buffer holds complete network
packets before decoding. If a packet arrives out of order but before its
playout deadline, DVM Console puts it back in sequence. If the deadline expires
first, playback continues and the decoder applies its normal loss concealment.
One missing packet cannot stall the call.

Adaptive mode measures arrival variation for each FNE connection and protocol.
At the start of a call, each receive stream takes a snapshot of the current
target. Simultaneous streams therefore keep separate sequencing and playout
clocks. The target rises immediately in whole-packet steps when late arrivals
need more headroom. It falls by one packet after three clean completed calls.
Packets that never arrive do not increase the target by themselves. Adaptive is
the default for new or unsaved settings. It starts with no added frames on a
clean connection and can learn up to nine.

Only protocol-aligned durations are offered:

- P25: zero through nine 180 ms LDUs; adaptive range 0 to 1620 ms.
- DMR: zero through nine 60 ms packets; adaptive range 0 to 540 ms.
- NXDN: zero through nine 80 ms packets; adaptive range 0 to 720 ms.

The fixed or learned duration is added to the decode and speaker path. Under
normal conditions, estimated packet-arrival-to-speaker latency is the stream's
jitter target plus about 80 to 110 ms. The audio device can add a route-dependent
delay that DVM Console cannot measure. Turning the jitter buffer off minimizes
latency but removes the chance to reorder packets.

Changing a selection saves that FNE's settings and recreates its active
listening and patch-source decode sessions.

Below the selectors, **Adaptive learned** shows each protocol's current adaptive
target. **Jitter effectiveness** counts delayed packets restored before playout
and expected packets that missed their deadlines. These values refresh with the
connection diagnostics rather than for every packet.

The FNE identity, jitter selectors, and connection actions wrap onto additional
rows when the window is narrow. One state-aware button alternates between
**Connect** and **Disconnect**; **Restart** remains separate.

Debug Logs summarize jitter evidence by receive stream instead of adding an
entry for every affected packet. They record the first event, periodic updates
during a long stream, and a final summary of reordered packets and missed
deadlines. Pipeline timing separates intentional jitter hold, worker backlog,
session-gate waits, clear or encrypted processing, and mixer admission. These
are components of `total FNE-to-mixer` time.

Timing high-water marks reset for each stream. Connection-level effectiveness
totals retain completed-call evidence until the FNE disconnects or its jitter
setting changes. Existing summaries and timing messages remain in Debug Logs
for the current application session, within the displayed 100 MB memory limit.
At the limit, the window discards the oldest entries and reports how many were
removed.

## Encryption Key Status

This opens the channel key-status section of **Connections**. The page shows
only key identifiers and availability; it never displays or logs key material.
For an unavailable local DMR key, it also shows the protocol, algorithm ID, and
key length required by the channel.

DVM Console restores selectable-encryption state independently of key arrival.
If a restored channel is secure and its key is available only through KMM, the
channel remains unavailable during the post-connect request. When the matching
key arrives, the encryption state refreshes and the restored **SECURE** control
appears. DVM Console does not save or display the key itself.

---

# Help menu

## Documentation

This opens the searchable documentation viewer, which renders headings, lists,
tables, links, and code blocks from the user guide.

## About

This shows the application version, seven-character commit ID, copyright, AGPL
license notice, project attribution, and repository links.
