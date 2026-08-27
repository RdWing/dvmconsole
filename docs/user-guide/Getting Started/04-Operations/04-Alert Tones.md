# Alert tones

The console supports three built-in alert tones, custom alert audio,
generated tones, DTMF, and QCII paging.

Use them for pages, alerts, and channel-hold tone behavior.

---

# Sending alert tones

The toolbar buttons **ALERT 1**, **ALERT 2**, and **ALERT 3** use DVM Console's
built-in tone generator and do not need external audio files.

As the main window narrows, the alert shortcuts move into **MORE** before
**TONES** or the configured clocks. The same three actions are available there
and follow the normal armed-resource and validation rules.

- **ALERT 1:** continuous 1000 Hz for 3 seconds.
- **ALERT 2:** alternating 1500 Hz and 800 Hz every 250 milliseconds for seven cycles.
- **ALERT 3:** eight 250-millisecond bursts of 1000 Hz, separated by 250 milliseconds of silence.

The tone generator outputs approximately -25 dBFS.

Arm `ALERT` on every resource that should carry Alert 1 through 3, custom alert
audio, generated tones, tone presets, or DTMF. Arm `PAGE` on every resource
that should carry a QCII page. Sending uses all armed resources in the selected
route.

Alert transmission uses the configured resource, system, talkgroup, mode, and
validation rules.

If the target TG is unavailable on the connected FNE, DVM Console blocks the
action and shows:

```
Target TG unavailable on FNE
```

---

# Tones in Console Settings

Open from:

```
Commands > Tones > QCII / alert tones
```

From the Tones page, operators can:

- view custom alert tones
- add a new alert tone
- delete a custom alert tone
- save and send DTMF or generated-tone presets
- send a Quick Call II two-tone page

The normal settings system saves these changes.

---

# Audio file requirements

DVM Console accepts PCM WAV or MPEG audio up to 30 seconds and converts it to
the 8 kHz mono transmit path. If decoding fails, it shows an error without
keying an `ALERT` resource.

Imported assets are sent as ordinary audio in every digital mode. DVM Console
does not reinterpret a steady section as a generated tone. Generated tones,
DTMF, and QCII continue to use their dedicated paths.

---

# Deleting alert tones

Deleting a custom alert tone removes it from both the list and saved settings.

Use the confirmation prompt to avoid accidental removal.

---

# Operational notes

- Alert tone audio is transmitted to all `ALERT`-armed resources when sent.
- QCII page audio is transmitted to all `PAGE`-armed resources.
- Alert tone sends do not bypass talkgroup validation.
- A custom asset is copied into application settings storage; the original source file is not required after import.
