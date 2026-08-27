# Overview

DVM Console NEO is an open-source DVM FNE operator console for macOS and
Windows. It puts live channels, patches, tones, recordings, and diagnostics in
one workspace. The Avalonia application runs on Apple Silicon and Intel Macs as
well as Windows x64.

This project is an independently maintained downstream version of
[DVMProject/dvmconsole](https://github.com/DVMProject/dvmconsole).

DVM Console NEO is for amateur and educational use. It is not for public- or
life-safety operation.

---

# Main concepts

## Systems

A system is an FNE peer connection defined in the codeplug. It has its own
address, port, peer ID, console RID, credentials, and optional RID alias file.

Click a system status card to connect or disconnect that FNE. Manual connection controls are also available under:

```
Tools > FNE Connection Manager
```

## Zones

Zones appear as tabs across the top of the channel area. They group channels by
dispatch area, agency, site, or operator role.

## Channels

A channel card maps a system to a talkgroup. Its controls cover receive
selection, local volume, PTT, transmit routing, page routing, alert routing, and
TAR.

The card displays the talkgroup and protocol together, for example:

```
TG 9990 - P25
```

## Groups

Define groups in the codeplug and manage them under **View > Groups**.

- Patch groups forward received audio between member channels.
- Multi-select groups key several member channels from one operator PTT.

Membership is saved separately from whether a patch is active.

---

# Main console

## Operator workspace

The main console has freeform channel cards grouped by system and zone. It saves
card positions and shows recent calls in the Activity sidebar.

Choose **View > Engineering Health** to open the optional telemetry rail. It is
hidden by default and has no transmit, mute, routing, or recording controls. The
rail reports receive queue pressure and latency, microphone state, generation
and cadence, transmit backlog, TAR finalization and catalog work, route
recovery, and connection health for the selected system. DVM Console stores its
visibility and height in `OperatorView.json`; this does not change
`UserSettings` schema 6 or channel-card positions.

## Channel interaction

Click a channel card outside its controls to turn local receive audio on or off.

The bottom row contains:

- `PTT`: transmit on that channel only.
- `TX`: include the channel in global PTT.
- `PAGE`: include the channel in QCII pages.
- `ALERT`: include the channel in alert tones, DTMF, and custom alert audio.
- `TAR`: enable or disable Talkgroup Audio Recorder capture for the channel.

Purple means a route or recorder is enabled. Gray means it is disabled.

By default, the Activity sidebar shows recent calls from RX-enabled channel
cards. Use **Active** / **All** to exclude or include channels with RX disabled.
The separate **Zone Wide** / **System Wide** control limits the selection to the
current zone or expands it to the whole system. Use the arrow at the edge to
collapse the sidebar. Double-click the Activity heading to open Event History
in Console Settings. Double-click a call with a TAR recording to select that
file in Finder or File Explorer. New calls do not move the entry being read when
the sidebar is scrolled away from the top. At the top, the sidebar continues to
follow new calls.

Enabled clocks sit immediately to the left of **Keep Mic Warm** and the three
output-mute controls. As the window narrows, the flexible space after **Help**
collapses first. Alert shortcuts then move into **MORE**, followed by **TONES**
and the clocks. This keeps controls within the window at the configured interface
scale and with any number of enabled clocks.

The three built-in alert patterns are available from the toolbar or **MORE**.
**TONES** opens the Tones page in Console Settings. The speaker button without a
system or zone prefix mutes all live RX output but leaves decoding and TAR
recording active.

After a normal exit, DVM Console restores the main window's previous size and
position at the next launch. Closing while maximized or minimized does not
replace those saved bounds. If the display layout changes and the title bar
would be unreachable, DVM Console ignores the saved position.

---

# Console settings

Several menu entries open Console Settings on a specific page. The window is
modeless, so it can stay open while you use the main console. Search the left
navigation to find a section.

Pages include:

- Audio device and per-channel routing
- DTMF, generated tones, QCII, and custom alert audio
- Web streams
- Talkgroup Audio Recorder configuration and playback
- Event history tools
- Patch and multi-select groups
- FNE connections and encryption key status
- Appearance, clocks, widgets, startup behavior, and PTT settings

---

# Subscriber commands

P25 Page, Radio Check, Inhibit, and Uninhibit are available under **Commands**.

DVM Console validates the 24-bit subscriber ID and requires a connected FNE and
configured source RID. It also asks for confirmation before Inhibit or
Uninhibit. Sent commands appear in the command audit history.

Subscriber acknowledgement decoding is not implemented. A successful send
means the command was submitted to the connected FNE; it does not mean the
target radio acknowledged it.

---

# Diagnostics

Open the live debug log viewer from:

```
View > Debug Logs
```

The viewer captures FNE messages, searches each entry for all entered terms, and
exports redacted logs for troubleshooting. **Clear Text** clears the search but
does not delete captured entries. Logs last for the current application session
and share a 100 MB in-memory limit. When the log reaches that limit, DVM Console
discards the oldest entries first. If the viewer is scrolled away from the
newest entry, new traffic does not move the line being read.

If the application closes without an error dialog, inspect `LastCrash.log`
before restarting:

- macOS: `~/Library/Application Support/DVMProject/dvmconsole/`
- Windows: `%APPDATA%\DVMProject\dvmconsole\`

---

# Project notes

- DVMConsole connects to DVM FNE peers; it does not directly control base or
  mobile radios.
- Operator audio supports DMR, P25 Phase 1, and NXDN 4800. NXDN 9600/EFR and
  P25 Phase 2 transport are not implemented.
- DVM Console NEO is for amateur and educational use. It is not for public- or
  life-safety operation.

---

# License

DVMConsole is free software under the GNU Affero General Public License,
version 3. See the repository `LICENSE` file for the complete terms.
