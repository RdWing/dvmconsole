# Console Operation

This page covers common operator workflows on the main console.

---

# Selecting Resources

Click a resource card to select or deselect it.

Selected resources are monitored locally. If a receiving resource is deselected while active, local monitoring for that resource stops and the card RX state is cleared.

Use **Channels > Disable all receive** to stop local monitoring on every channel.

Each transmit-capable card has four independent routing controls:

- `TX` includes the resource in global or multi-channel voice PTT.
- `PAGE` includes the resource when a QCII page is sent.
- `ALERT` includes the resource when DTMF, a generated tone, a tone preset, or an alert audio file is sent.

Purple indicates an armed selector. Gray indicates that selector is not armed.
The selectors do not use check marks, and moving the pointer over an armed
selector does not change its state. Click the selector to arm or disarm only
that routing path.

The `TAR` button occupies the same control row. It enables or disables local
Talkgroup Audio Recorder capture for that resource. TAR can record inbound
calls while the card is not selected for live RX. Speaker monitoring remains
controlled independently by clicking the card, so there is no separate listen
button.

The talkgroup and protocol are shown together as `TG 9990 - DMR` or
`TG 9990 - P25`.

---

# Resource Card Sizes

Resource card size is defined in the codeplug with `card_size`.

Supported sizes:

- `small`: compact status/PTT card. Volume, alert tone select, channel marker, and call history buttons are hidden.
- `normal`: default card size.
- `large`: larger card with larger text and controls.

If `card_size` is omitted, the console uses `normal`.

---

# PTT

Use a resource card PTT button to transmit on that resource.

The console supports normal press-and-hold PTT and toggle PTT mode.

Toggle PTT mode is controlled by:

```
Settings > Toggle Push To Talk Mode
```

Toggle PTT is off by default. If changed, the preference is saved.

---

# Selectable Encryption

Secure-capable P25, DMR, and NXDN resources may show **SELECT** in the card text area.

Click **SELECT** to toggle that resource between encrypted and clear console transmit. The choice is saved by system/talkgroup and restored on the next startup.

If the resource does not show **SELECT**, encryption behavior is fixed by the codeplug.

---

# Global PTT

Global PTT keys every channel with `TX` armed. Active-system PTT keys only the `TX`-armed channels in the system tab that is active when PTT starts. At least one transmit-capable channel must be armed in the applicable scope before PTT can begin.

Choose separate keys under **Channels > Global PTT key** and **Channels > Active-system PTT key**, or configure both under **Console Settings > PTT**. Space and F1 through F19 are supported, and each enabled binding must use a unique key. Both bindings use the same saved press-and-hold or toggle PTT setting. On macOS, OS-global capture may require Accessibility or Input Monitoring permission. When global capture is unavailable, the keys still work while the application has keyboard focus.

Under **Console Settings > PTT > Serial hardware PTT**, select **Limit serial PTT to TX-selected resources in the active system** to give the serial device the same active-system scope. Leave it clear for the serial device to key every `TX`-selected resource across systems.

On macOS, **Console Settings > PTT > Keyboard PTT > Request macOS keyboard
access** asks the system for Input Monitoring access again. If macOS has already
recorded a denial, enable DVM Console manually under **System Settings > Privacy
& Security > Input Monitoring**.

The keyboard PTT keys remain active while the modeless **Console Settings** window
has focus. Console Settings can stay open while the operator selects resources or
uses other controls in the main window.

The talk permit tone uses the selected output device for card, global, and
active-system PTT. Global and active-system keybinds complete this cue in both
press-and-hold and toggle mode before microphone audio is released. Use
**Audio > Test talk permit tone** when checking the route.

---

# Transmit Tail

When PTT is released, the console briefly holds transmit before sending call-end signaling.

This short de-key tail helps prevent clipped final syllables and final voice frames.

The tail affects the real transmit path, not only the UI.

---

# Talkgroup Validation

When a user tries to transmit or use a talkgroup, the console checks the active talkgroup rules received from the connected FNE.

If the talkgroup is unavailable on that FNE, the action is blocked and this warning is shown:

```
Target TG unavailable on FNE
```

This validation is per system and applies to P25 and DMR resources.

---

# RX Activity

When selected resources receive traffic, the card shows RX activity and source information.

Tabs show an audio activity icon when a resource on that tab is receiving. Long tab names are trimmed so the activity icon remains visible.

---

# Web Stream Chips

Codeplug-defined web stream chips appear on the zone tab where they are configured.

Click a stream chip to start or stop playback. Streams load in the off state unless **Restore Selected Channels On Startup** is enabled and the stream was active at shutdown.

Stream chips use a compact volume slider. User volume changes are saved by stream name, and the chip turns green when audio is detected.

When a stream is starting, the chip turns amber while connecting. The console tries up to three connection attempts before marking the stream down.

If a stream URL is unreachable or cannot be decoded, the chip turns red and shows `Down`. Click a down stream once to return it to the off state.

Protected streams can use HTTP Basic Auth through `authUsername` and `authPassword` fields in the codeplug.

Web streams are local monitor widgets. They are not patch or multi-select members.

The reset tab layout action also moves web stream chips on the active tab and saves their new positions.

---

# Sticky Selected Channels

Controlled by:

```
Settings > Restore Selected Channels On Startup
```

When enabled:

- selected resources are restored on startup
- saved volume for restored resources is restored
- configured encrypted P25 resources request keys through KMM after their FNE connects and use the local key file as fallback

When disabled:

- resources start unselected
- per-resource volumes start at default

---

# Card Indicator Icons

The top-right card indicator can show membership state.

Common meanings:

- active patch member
- disabled patch member
- multi-select member

Multi-select membership takes visual priority when both patch and multi-select memberships apply.
