# Talkgroup Audio Recorder

Talkgroup Audio Recorder (TAR) saves selected calls as local `.opus` files. Each
recording contains its catalog metadata, so new recordings do not need a
separate `.json` sidecar.

TAR saves received audio before the optional RX filters and compressor. Those
controls change local listening only; they do not alter your recordings.

---

# Opening TAR

Use either of these menu paths:

```text
Tools > Talkgroup Audio Recorder > Viewer
Tools > Talkgroup Audio Recorder > Configuration
```

**Viewer** opens Event History for search, playback, export, and file actions.
**Configuration** opens the Recorder page, where you choose the recording
location, retention period, channels, and ignored subscriber IDs.

---

# What TAR records

TAR creates one file per call. It can record:

- received audio on TAR-enabled channels
- console transmit audio sent through TAR-enabled channels

Recording does not depend on local speaker selection. A channel can record an
incoming call while its live RX audio is off or muted. System, zone, and global
output mute controls affect speaker playback only.

---

# Recording location

DVM Console stores recordings in its application-data `Recordings` folder by
default. On desktop systems, the Recorder page can use an external folder
instead:

1. Select **Browse…** and choose a folder.
2. Select **Apply location**.

DVM Console validates the location before switching to it. If the change fails,
the current recording location stays active.

---

# Folder ownership and recording errors

Only one running console can record into a given root folder. Another console
can read completed recordings, but cannot record there while the first owns
it. Ownership remains in place while background finalization finishes.

If the Recorder page reports that another console owns the folder, close that
console and let it finish, or choose a different recording root. Switching
configurations within one console retries ownership after the previous session
releases it. Do not delete recovery files to work around an ownership error.

A recording error appears on the affected channel. Check available
space, folder access, and the Recorder page's availability message, then
re-enable TAR if the channel was disabled by an earlier error. A successful
recording clears the previous error. Failure on one recording does
not prevent cleanup of other calls or stop transmit audio.

---

# Enabling recording

The Recorder page groups channels by FNE system. For each channel:

- Select the recording button to turn TAR on or off.
- Enter radio IDs in **Ignored RIDs, comma separated** when calls from those
  subscribers should not be recorded on that channel.
- Select **Save ignored RIDs** after changing the list.

Ignored IDs can be separated by commas, spaces, or semicolons. For example:

```text
1001, 1002 1003;1004
```

The same TAR control appears in the channel card's bottom row. Its colored state
shows whether recording is armed. Clicking the channel card still controls live
speaker playback separately.

---

# Event History

Event History combines current-session calls and events with TAR recordings
found in the recording catalog. Open it from **View > History**, the Activity
heading, or **Tools > Talkgroup Audio Recorder > Viewer**.

Each row can show the time, system, channel, direction, protocol, duration,
encryption state, source information, and talkgroup. Rows with a recording have
these actions:

- **Play** starts playback through the master output device.
- **Stop** stops recording playback.
- **Open** shows the recording folder. Finder and File Explorer also select the
  file.
- **Delete** removes that recording after confirmation.

The compact Activity sidebar separates calls by local date. A playing recording
has an emphasized border and a **Playing** label. Use the same row's playback
button to stop it. The status bar identifies the recording by channel and call
time.

**Export CSV…** exports the loaded Event History entries. **Clear session**
clears the current History list but does not delete TAR files. A completed TAR
recording can remain in History after its live call row ages out of the
in-memory session list.

---

# Search and filters

The search box checks events, calls, aliases, IDs, filenames, and diagnostics.
Expand **Advanced filters** to narrow the list by:

- direction
- protocol
- encryption state
- system
- channel
- talkgroup ID
- subscriber ID
- alias
- start and end date

Select **Clear filters** to reset the search and advanced filters. The Clear and
Secure filters include only recordings with that confirmed encryption state.
A recording marked Unknown does not appear in either result.

---

# Retention

**Retention days** applies to the recording catalog as a whole. Enter a whole
number from 0 to 3650 and select **Apply and prune**.

- `0` disables automatic age-based pruning.
- A positive value removes completed recordings whose UTC end time is older
  than the selected number of days.
- Applying a shorter period can delete older recordings immediately.

DVM Console also checks retention when it loads the recording catalog.

---

# Recording folder structure

TAR organizes recordings by local date and system:

```text
<Recording root>/
  YYYY-MM-DD/
    <SystemName>/
      <time>_<system>_<talkgroup>_<rid>_<UNKNOWN-or-CLEAR-or-SECURE_algorithm>_<stream>.opus
```

Generated names avoid characters and reserved names that cause problems when
recordings are moved between supported desktop platforms. Existing recordings
are left in place. App-owned recording and recovery files use private file
permissions where supported, including in a custom root; the console does not
recursively change permissions on your chosen folder.

The date folder and filename use local time. Embedded metadata keeps UTC start
and end timestamps. `UNKNOWN` means the call ended before DVM Console received
enough on-air metadata to classify it as clear or secure.

---

# Embedded metadata

New recordings store catalog metadata in an OpusTags field inside the `.opus`
file. The metadata can include:

- recording direction (`RX` or `TX`)
- protocol
- UTC start and end time
- duration
- system, channel, and talkgroup
- subscriber ID and resolved alias
- encryption state, algorithm, and key ID when known
- stream ID and receive episode ID when available
- file size, sample rate, bit depth, and channel count

The recording does not embed the local root or catalog path. Event History
reconstructs those values from the file it opened.

DVM Console does not migrate legacy `.json` sidecars. An older `.opus` file
appears in the catalog only when it already contains supported embedded
metadata.

---

# Recording finalization

TAR trims leading and trailing silence, encodes 9 kbps mono Opus in VOIP mode,
and embeds metadata when the call closes. Finalization runs in the background.
During shutdown, DVM Console first gives pending call-end signals and
recordings time to finish. It then cancels remaining receive work and proceeds
with shutdown, with time limits on each step. A stalled recording has its own
timeout, and its recovery files remain available for a later attempt.
