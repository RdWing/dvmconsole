# Overview

The Digital Voice Modem Desktop Dispatch Console is a cross-platform operator console for monitoring and transmitting on DVM FNE talkgroups.

The Avalonia application runs on Apple Silicon and Intel macOS plus Windows
x64. It keeps the established DVM Console codeplug and FNE behavior while
providing the same operator workflow across supported platforms.

DVM Console is released as a cross-platform Avalonia application. Test every build with the intended FNE, audio devices, and operator workflow before operational use.

---

# Main Concepts

## Systems

A system is an FNE peer connection defined in the codeplug. Each system has its own address, port, peer ID, console RID, credentials, and optional RID alias file.

Click a system status card to connect or disconnect that FNE. Manual connection controls are also available under:

```
Tools > FNE Connection Manager
```

## Zones

Zones become tabs across the top of the channel area. Use zones to group channels by dispatch area, agency, site, or operator role.

## Channels

A channel card maps a system to a talkgroup. The card provides receive selection, local volume, PTT, transmit routing, page routing, alert routing, and TAR control.

The card displays the talkgroup and protocol together, for example:

```
TG 9990 - P25
```

## Groups

Groups are defined in the codeplug and managed under **View > Groups**.

- Patch groups forward received audio between member channels.
- Multi-select groups key several member channels from one operator PTT.

Membership is saved separately from whether a patch is active.

---

# Main Console

Click a channel card outside its controls to enable or disable local receive audio.

The bottom row contains:

- `PTT`: transmit on that channel only.
- `TX`: include the channel in global PTT.
- `PAGE`: include the channel in QCII pages.
- `ALERT`: include the channel in alert tones, DTMF, and custom alert audio.
- `TAR`: enable or disable Talkgroup Audio Recorder capture for the channel.

Purple means a route or recorder is enabled. Gray means it is disabled.

The Activity sidebar shows recent calls for RX-enabled channel cards by default.
Use **Active** / **All** to include or exclude channels that are not enabled for
RX. The separate **Zone Wide** / **System Wide** control limits the selected
set to the current zone. Collapse the sidebar with the arrow at
its edge. Double-click the Activity heading to open Event History in Console
Settings. Double-click a call with a TAR recording to show that file selected
in Finder or File Explorer.

The toolbar provides the three original alert patterns and a **TONES** button that opens Console Settings directly to the Tones page.

When DVM Console closes normally, it remembers the main window's last normal
size and position for the next launch. Closing while maximized or minimized
does not replace those normal bounds, and a saved position is ignored if a
display-layout change would leave the title bar unreachable.

---

# Console Settings

Several menu entries open the modeless Console Settings window on a specific page. The window can remain open while the main console is used.

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

# Subscriber Commands

P25 Page, Radio Check, Inhibit, and Uninhibit commands are available under **Commands**.

The console validates the 24-bit subscriber ID, requires a connected FNE and configured source RID, and requires confirmation before Inhibit or Uninhibit. Sent commands appear in the command audit history.

Subscriber acknowledgement decoding is not yet implemented. A successful send confirms that the command was submitted to the connected FNE, not that the target radio acknowledged it.

---

# Diagnostics

Open the live debug log viewer from:

```
View > Debug Logs
```

The viewer captures FNE messages, supports an all-terms search across each log
entry, and can export a redacted log for troubleshooting. **Clear Text** clears
the entered search without deleting the captured entries.

If the application closes without an error dialog, inspect `LastCrash.log` before restarting:

- macOS: `~/Library/Application Support/DVMProject/dvmconsole/`
- Windows: `%APPDATA%\DVMProject\dvmconsole\`

---

# Project Notes

- DVMConsole connects to DVM FNE peers; it does not directly control base or mobile radios.
- Operator audio supports DMR, P25 Phase 1, and NXDN 4800. NXDN 9600/EFR and P25 Phase 2 transport are not implemented.
- This software is intended for amateur and educational use. Any other use is at the user's discretion and risk.

---

# License

DVMConsole is free software licensed under the GNU Affero General Public License, version 3. See the repository `LICENSE` file for the complete terms.
