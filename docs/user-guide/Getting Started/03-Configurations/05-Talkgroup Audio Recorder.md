# Talkgroup Audio Recorder

This page covers the built-in **Talkgroup Audio Recorder (TAR)** feature.

TAR records selected talkgroups to compact local `.opus` files. The catalog metadata is embedded in each recording, so new recordings do not need a separate `.json` file.

---

# Open TAR

Open TAR from:

```
Tools > Talkgroup Audio Recorder > Viewer
Tools > Talkgroup Audio Recorder > Configuration
```

Use **Viewer** to review and play recordings.

Use **Configuration** to choose the recording folder and decide which talkgroups TAR records.

---

# What TAR Records

TAR records per-call audio, not one long continuous file.

It can record:

- received call audio on TAR-enabled talkgroups
- console-originated transmit audio on TAR-enabled talkgroups

TAR only records when both of these are true:

- TAR is enabled for the talkgroup in TAR Configuration
- the resource is selected in the main console so the console is actively monitoring or using that path

If a talkgroup is TAR-enabled but the resource is not selected, TAR does not capture live call audio for that resource.

---

# Recording Folder

TAR requires a valid recording folder.

The default location is the `DVMConsole/TAR` folder under the current user's Documents folder.

You can change this in:

```
Tools > Talkgroup Audio Recorder > Configuration
```

If the folder does not exist, the console creates it when TAR settings are saved.

---

# Enable Recording

In the TAR Configuration window:

- channels are grouped by console tab
- each row is keyed by TGID
- click the `TAR` control to enable or disable recording
- optionally enter ignored subscriber IDs in **Ignore RIDs**

After TAR is enabled for a talkgroup, the operator must still select that resource on the main console for TAR to record live activity on it.

Ignored subscriber IDs are useful for excluding known announcement or non-essential sources from TAR on a specific talkgroup.

Enter multiple ignored subscriber IDs separated by commas, spaces, or semicolons.

Example:

```text
1001, 1002 1003;1004
```

---

# Channel Indicator

When TAR is enabled for a talkgroup, the resource card shows a purple:

```text
TAR
```

button in the card's bottom control row.

If TAR is not enabled for that talkgroup, nothing is shown.

---

# Viewer Basics

The TAR Viewer opens with the newest recordings first.

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

---

# Advanced Filters

Expand **Advanced Filters** in the TAR Viewer to narrow results.

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

TAR retention is real file cleanup.

For each TAR-enabled talkgroup, **Keep Days** controls how long recordings are kept on disk.

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

# Recording Folder Structure

TAR stores recordings in date- and system-organized folders for easier browsing.

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

The date folder and filename time use the local system timezone. The metadata retains UTC start and end timestamps.

---

# Metadata

Each new recording stores its catalog metadata in an OpusTags field inside the `.opus` file.

Legacy `.json` sidecars are not scanned, migrated, or deleted. Older recordings appear in the TAR Viewer only when their metadata is already embedded in the `.opus` file.

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

This metadata is used by the TAR Viewer and can also be read by tools that inspect OpusTags.

---

# Notes

- TAR records to `.opus` without requiring an external media process
- TAR targets 9 kbps mono Opus in VOIP mode with variable bitrate enabled
- TAR embeds catalog metadata in each new `.opus` recording
- TAR trims leading and trailing silence before finalizing the saved file
- TAR viewer playback uses the configured master output device
- deleting a recording from the Viewer removes only its `.opus` file
