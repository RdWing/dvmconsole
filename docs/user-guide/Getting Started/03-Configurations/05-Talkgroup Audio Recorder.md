# Talkgroup Audio Recorder

The built-in **Talkgroup Audio Recorder (TAR)** saves selected talkgroups as
local `.opus` files. Each new recording contains its catalog metadata and does
not need a separate `.json` file.

---

# Opening TAR

Open TAR from:

```
Tools > Talkgroup Audio Recorder > Viewer
Tools > Talkgroup Audio Recorder > Configuration
```

Use **Viewer** to review and play recordings. Use **Configuration** to select the
recording folder and the talkgroups to record.

---

# What TAR records

TAR creates one recording per call instead of one continuous file.

It can record:

- received call audio on TAR-enabled talkgroups
- console-originated transmit audio on TAR-enabled talkgroups

Recording a received call does not depend on live speaker selection. Arming TAR
for a resource is enough to decode and record its inbound calls; the card does
not also need RX selected. Speaker playback remains off until the operator
selects the card separately.

TAR records console-originated transmit audio when an armed resource takes part
in the transmission.

---

# Recording folder

TAR requires a valid recording folder.

The default location is `DVMConsole/TAR` under the current user's Documents
folder.

You can change this in:

```
Tools > Talkgroup Audio Recorder > Configuration
```

If the folder does not exist, DVM Console creates it when you save the TAR
settings.

---

# Enabling recording

In the TAR Configuration window:

- channels are grouped by console tab
- each row is keyed by TGID
- click the `TAR` control to enable or disable recording
- optionally enter ignored subscriber IDs in **Ignore RIDs**

After you enable TAR for a talkgroup, recording starts automatically when
matching traffic arrives. Selecting the resource card for live listening is a
separate choice.

Use ignored subscriber IDs to exclude known announcements or other unwanted
sources from TAR on a specific talkgroup.

Enter multiple ignored subscriber IDs separated by commas, spaces, or semicolons.

Example:

```text
1001, 1002 1003;1004
```

---

# Channel indicator

When TAR is enabled for a talkgroup, the resource card shows a purple:

```text
TAR
```

button in the card's bottom control row.

If TAR is disabled for the talkgroup, the button is hidden.

---

# Viewer basics

The TAR Viewer lists the newest recordings first.

Default fields:

- Time
- Duration
- Channel
- TG
- Source ID
- Alias

Use the **Columns** button in the TAR Viewer to show or hide additional fields such as:

- Direction
- Protocol
- System
- Encryption

Viewer actions:

- Refresh
- Play
- Stop
- Open Folder
- Delete

Completed TAR recordings remain available in History after their live call rows
age past the in-memory session limit. This changes only how History represents
the recording. File deletion still follows the configured TAR retention policy.

---

# Advanced filters

Expand **Advanced Filters** in the TAR Viewer to narrow the recording list.

Available filters include:

- free text search across key fields
- Direction
- Protocol
- Encryption
- System
- Channel
- Talkgroup ID
- Subscriber ID
- Alias
- Start Date
- End Date

Use **Clear Filters** to reset the current filter set.

---

# Retention

For each TAR-enabled talkgroup, **Keep Days** controls how long recordings stay
on disk. Retention cleanup deletes files.

Behavior:

- TAR scans metadata embedded in saved `.opus` recordings
- TAR checks the current configured retention for that TGID
- if a recording is older than the allowed age, TAR deletes the `.opus` file

Important notes:

- `0` days means keep recordings indefinitely
- retention is based on the recording end time in UTC
- cleanup uses the **current** TAR config for that TGID at cleanup time
- if you later shorten retention for a talkgroup, older existing recordings for that talkgroup can be deleted on the next cleanup pass

Cleanup runs when:

- TAR configuration is saved
- the console loads a codeplug and TAR initializes

---

# Recording folder structure

TAR organizes recordings by date and system.

Folder layout:

```text
<TAR Root>\
  YYYY-MM-DD\
    <SystemName>\
      <time>_<system>_<talkgroup>_<rid>_<CLEAR-or-SECURE_algorithm>_<stream>.opus
```

Example structure:

```text
TAR\
  2026-05-02\
    System1\
      221530125_System1_3100_1001_SECURE_AES_42.opus
```

The date folder and filename use the local system time zone. Embedded metadata
keeps the UTC start and end timestamps.

---

# Metadata

Each new recording stores its catalog metadata in an OpusTags field inside the
`.opus` file.

TAR does not scan, migrate, or delete legacy `.json` sidecars. An older
recording appears in the Viewer only if its `.opus` file already contains the
metadata.

Metadata includes:

- recording direction (`RX` or `TX`)
- protocol
- UTC start time
- UTC end time
- duration
- system name
- channel name
- talkgroup ID
- subscriber ID
- subscriber alias
- encryption status
- encryption algorithm, when known
- encryption key ID, when known
- stream ID
- file size
- sample rate / bit depth / channel count

The TAR Viewer reads this metadata. Other tools that inspect OpusTags can read
it as well.

---

# Notes

- TAR records to `.opus` without requiring an external media process
- TAR targets 9 kbps mono Opus in VOIP mode with variable bitrate enabled
- TAR embeds catalog metadata in each new `.opus` recording
- TAR trims leading and trailing silence before finalizing the saved file
- TAR viewer playback uses the configured master output device
- deleting a recording from the Viewer removes only its `.opus` file
