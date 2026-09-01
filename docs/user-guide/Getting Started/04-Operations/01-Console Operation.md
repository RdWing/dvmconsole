# Console operation

This page describes the main console and its common operator workflows.

---

# Operator workspace

The main console uses the Cards workspace by default. Cards are organized by
system and zone, and each managed configuration keeps its own saved positions,
configured sizes, and operator state.

Choose **View > Channel view > List** for a compact virtualized renderer grouped
by system and zone. DVM Console temporarily forces List below 600 logical pixels
without changing the saved desktop preference or card positions.

The Activity sidebar shows recent calls and subscriber-command audit entries.
Use the arrow in its header to collapse or expand it. At the top of the list,
the view follows new calls. After you scroll down, incoming rows do not move the
call being read.

Choose **View > Engineering Health** to inspect runtime telemetry. This
resizable horizontal rail is hidden by default. It reports receive pressure and
latency, microphone freshness, generation, cadence and faults, transmit
backlog, TAR finalization and catalog work, route recovery, and connection
health. It has no PTT, mute, routing, or recording controls.

## List view

Each collapsed List row shows RX state, channel/talkgroup/protocol, current
state, last caller, the same threshold-colored RMS/peak meter, and fail-safe
PTT. RX, PTT, and selector controls do not also expand the row.

Select the non-control area to expand the row. The expanded area provides the
channel volume control and compact operational state. Selectable-encryption
channels use the same **SECURE/CLEAR** transmit choice as Cards. Balance and
output route remain in Audio settings so the line layout stays compact.

The volume slider defaults to its center position and uses the same slightly
sticky center behavior as Cards. Expansion is session-only and never changes a
saved card position. Any row recycling, renderer change, navigation, session
replacement, or shutdown releases held PTT through the same idempotent
controller used by Cards.

---

# Selecting resources

Click a resource card to select or deselect it.

Selected resources play locally. Deselecting an active receiving resource stops
its local playback and clears the card's RX state.

Use **Channels > Disable all receive** to stop local playback on every channel.

Each transmit-capable card has four independent routing controls:

- `TX` includes the resource in global or multi-channel voice PTT.
- `PAGE` includes the resource when a QCII page is sent.
- `ALERT` includes the resource when DTMF, a generated tone, a tone preset, or an alert audio file is sent.

Purple indicates an armed selector. Gray indicates that selector is not armed.
The selectors do not use check marks. Hovering over an armed selector does not
change it. Click a selector to arm or disarm that routing path.

The `TAR` button is in the same control row and turns local recording on or off
for that resource. TAR can record inbound calls when the card is not selected
for live RX. Clicking the card controls speaker playback separately; there is
no additional listen button.

The talkgroup and protocol are shown together as `TG 9990 - DMR` or
`TG 9990 - P25`.

The narrow bar above the volume slider shows channel audio level. Receive and
transmit use the same -50 to 0 dBFS scale. The colored fill is a 50 ms RMS
reading, and the separate peak marker holds for 750 ms. The marker is white
below -12 dBFS, yellow from -12 to -6 dBFS, and red at -6 dBFS or above. The
meter is informational; it does not change gain, AGC, transmitted audio, or
recorded audio.

---

# Resource card sizes

Resource card size is defined in the codeplug with `card_size`.

Supported sizes:

- `small`: compact status/PTT card. Volume, alert tone select, channel marker, and call history buttons are hidden.
- `normal`: default card size.
- `large`: larger card with larger text and controls.

If `card_size` is omitted, DVM Console uses `normal`.

---

# PTT

Use a resource card PTT button to transmit on that resource.

DVM Console supports press-and-hold and toggle PTT.

Toggle PTT mode is controlled by:

```
Settings > Toggle push-to-talk mode
```

Toggle PTT is off by default. If changed, the preference is saved.

A channel keyed in toggle mode remains keyed when DVM Console loses window
focus. Suspension, session replacement, shutdown, or pressing its PTT control
again releases the channel.

PTT is unavailable while a channel is receiving. Clicking its disabled PTT
control does not change the channel's RX selection. When PTT is released, DVM
Console stops accepting microphone input, sends the accepted queue at its
normal cadence, and then ends the radio call.

---

# Selectable encryption

Secure-capable P25, DMR, and NXDN resources may show **SELECT** in the card text
area.

Click **SELECT** to switch the resource between encrypted and clear transmit.
DVM Console saves the choice by system and talkgroup and restores it at the
next startup.

This choice affects transmit only. Clear receive traffic remains audible while
either **SECURE** or **CLEAR** is selected. Encrypted receive traffic plays when
its on-air metadata identifies an available key.

If the resource does not show **SELECT**, encryption behavior is fixed by the codeplug.

---

# Global PTT

Global PTT keys every channel with `TX` armed. Active-system PTT keys only the
armed channels in the system tab that is active when PTT starts. PTT requires
at least one transmit-capable channel in the applicable scope.

Choose separate keys under **Channels > Global PTT key** and **Channels >
Active-system PTT key**, or configure both under **Console Settings > PTT**.
Space and F1 through F19 are supported, and each enabled binding must use a
unique key. Both bindings use the same saved press-and-hold or toggle PTT
setting. On macOS, OS-global capture may require Accessibility or Input
Monitoring permission. When global capture is unavailable, the keys still work
while the application has keyboard focus.

The focused-window Space binding does not consume Space while an editable field
or ordinary interactive control has focus. OS-global capture remains available
where supported. During transmit, the status field at the bottom identifies
whether PTT came from the window-local keyboard, OS-global keyboard, serial
hardware, or a channel control.

Under **Console Settings > PTT > Serial hardware PTT**, select **Limit serial
PTT to TX-selected resources in the active system** to give the serial device
the same active-system scope. Leave it clear for the serial device to key every
`TX`-selected resource across systems.

Serial and OS-global keyboard PTT are desktop-only capabilities. On-screen PTT
does not depend on a physical input source.

On macOS, **Console Settings > PTT > Keyboard PTT > Request macOS keyboard
access** asks the system for Input Monitoring access again. If macOS has already
recorded a denial, enable DVM Console manually under **System Settings > Privacy
& Security > Input Monitoring**.

Keyboard PTT remains active while the modeless **Console Settings** window has
focus. The window can stay open while the operator selects resources or uses
the main console.

The talk permit tone uses the selected output device for card, global, and
active-system PTT. Global and active-system keybinds complete this cue in both
press-and-hold and toggle mode before microphone audio is released. Open
**Audio > Audio settings** and select **Test talk permit tone** when checking
the route.

---

# Transmit tail

After PTT is released, DVM Console briefly holds transmit before sending
call-end signaling.

The short de-key tail prevents the final syllables and voice frames from being
clipped. It is part of the transmit path, not a UI delay.

---

# Talkgroup validation

DVM Console treats each FNE-sourced talkgroup table as authoritative for
transmit targets.

DMR channels must match both the talkgroup and timeslot. P25, NXDN, and analog
channels are matched by destination ID. The same check applies to channel PTT,
multi-channel PTT, patches, alerts, pages, DTMF, and generated tones.

If a target is not permitted, DVM Console disables its PTT control and shows a
warning such as:

```
FNE talkgroup table does not allow Dispatch (TG 748, TS2); PTT disabled.
```

If a refreshed table removes an active target, DVM Console ends the affected
console or patch transmission cleanly. The target becomes available again when
a later authoritative table permits it.

---

# RX activity

When a selected resource receives traffic, its card shows RX activity and
source information.

Tabs show an audio activity icon when a resource on that tab is receiving. Long
tab names are trimmed so the activity icon remains visible.

---

# Web stream chips

Codeplug-defined web stream chips appear on their configured zone tabs.

Click a stream chip to start or stop playback. Streams load in the off state
unless **Restore selected channels on startup** is enabled and the stream was
active at shutdown.

Stream chips use a compact volume slider. User volume changes are saved by
stream name, and the chip turns green when audio is detected.

The chip turns amber while connecting. DVM Console tries up to three times
before marking the stream down.

Click an amber stream chip to cancel the pending connection or decoder startup.
The chip returns to the off state without waiting for that startup to finish.

If a stream URL is unreachable or cannot be decoded, the chip turns red and
shows `Down`. Click it once to turn the stream off.

Protected streams can use HTTP Basic Auth through `authUsername` and `authPassword` fields in the codeplug.

Web streams are local monitor widgets. They are not patch or multi-select members.

The reset tab layout action also moves web stream chips on the active tab and saves their new positions.

---

# Restoring selected channels

Controlled by:

```
Settings > Restore Selected Channels On Startup
```

When enabled:

- selected resources are restored on startup
- saved volume for restored resources is restored
- configured encrypted P25 resources request keys through KMM after their FNE
  connects and use the local key file as fallback

When disabled:

- resources start unselected
- per-resource volumes start at default

---

# Card indicator icons

The top-right card indicator can show membership state.

Common meanings:

- active patch member
- disabled patch member
- multi-select member

Multi-select membership takes visual priority when both patch and multi-select memberships apply.
