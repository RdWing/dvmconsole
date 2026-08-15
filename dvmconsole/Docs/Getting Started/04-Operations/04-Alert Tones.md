# Alert Tones

The console supports three built-in legacy alert tones, custom alert audio,
generated tones, DTMF, and QCII paging.

Alert tones are used for operator alerting workflows such as page/alert tones and channel hold tone behavior.

---

# Sending Alert Tones

The toolbar buttons **ALERT 1**, **ALERT 2**, and **ALERT 3** recreate the
original DVMConsole alert patterns with the console tone generator. They do not
depend on external WAV files and are not shortcuts to the QCII or DTMF tools.

- **ALERT 1:** continuous 1004 Hz for 3 seconds.
- **ALERT 2:** alternating 1500 Hz and 800 Hz every 250 milliseconds for seven cycles.
- **ALERT 3:** eight 250-millisecond bursts of 1004 Hz, separated by 250 milliseconds of silence.

The generated level matches the original files at approximately -25 dBFS.

Arm `ALERT` on every resource that should carry Alert 1 through 3, custom alert
audio, generated tones, tone presets, or DTMF. Arm `PAGE` on every resource
that should carry a QCII page. Sending uses all armed resources in the selected
route; it does not silently choose the last channel clicked.

Alert tone transmit still uses the configured resource, system, talkgroup, mode, and validation rules.

If the target TG is unavailable on the connected FNE, the console blocks the action and shows:

```
Target TG unavailable on FNE
```

---

# Alert Tone Manager

Open from:

```
Commands > Tones > QCII, DTMF, and Alerts
```

The Alert Tone Manager allows operators or admins to:

- view custom alert tones
- add a new alert tone
- rename an alert tone
- replace the backing audio file
- assign a tone to a tab
- delete a custom alert tone
- save changes without closing the manager

Changes are saved through the normal settings system.

---

# Audio File Requirements

Alert tone audio files must be compatible with the console audio pipeline.

The console accepts PCM WAV or MPEG audio up to 30 seconds and converts it to
the 8 kHz mono transmit path. If a file cannot be decoded, the console displays
an error without keying an `ALERT` resource.

---

# Deleting Alert Tones

Deleting a custom alert tone removes it from:

- the manager list
- the UI
- saved settings
- saved tab assignment and related custom tone state

Use the confirmation prompt to avoid accidental removal.

---

# Tab Assignment

Custom alert tones can be assigned to a tab/resource tab when supported by the current codeplug layout.

This keeps custom tones near the resources operators use with them.

---

# Operational Notes

- Alert tone audio is transmitted to all `ALERT`-armed resources when sent.
- QCII page audio is transmitted to all `PAGE`-armed resources.
- Alert tone sends do not bypass talkgroup validation.
- If an alert tone is renamed, the display name shown in the UI updates and persists after restart.
- If the backing file is replaced, verify the new file uses the required audio format.
