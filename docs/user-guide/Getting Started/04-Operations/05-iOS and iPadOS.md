# iOS and iPadOS

Your channels, patches, tones and recordings are available on iPhone and iPad,
with the same configuration format and radio/audio components as desktop. Tap
the gear in the top-right corner for Configuration Library, Studio, History,
audio settings and tools. You can keep listening while you browse Settings;
opening Settings releases manual transmit controls.

## Availability and first launch

Version 0.8.0 introduces iPhone and iPad support through a public TestFlight beta.

1. Open the [Console NEO beta invitation](https://testflight.apple.com/join/KuYtQqja) on your iPhone or iPad.
2. Install TestFlight if prompted, then accept the invitation and install Console NEO.
3. Open Console NEO and create or import a configuration as described below.

Updates and beta feedback go through TestFlight. An App Store release is planned
after beta testing. The app targets iOS/iPadOS 15 and newer; testing on older
devices is ongoing.

Open **Settings → Configuration Library** to create a configuration in Studio
or import an existing one. Add an FNE, zone and channel, review and save, then
open that saved configuration. **Settings → Edit active configuration** returns
to Studio. **Review & Save** opens a centered dialog with the changes to save.
Choose **Save** or **Cancel**. After saving, a second dialog offers to disconnect
and load the saved configuration. Canceling that second prompt keeps the saved
revision and leaves your current session running.

## Console layout

iPhone uses List in both orientations. Tap a channel row to reveal its volume
and TX, PAGE, ALERT and TAR controls. The slider adjusts that channel's volume.
Mute status appears beside the last caller without expanding the row during PTT.

iPad offers Cards and List. Your choice is saved for the configuration on this
device. A window narrower than 600 points temporarily uses List; widening the
window restores your choice. Configuration and layout settings do not sync
automatically between devices.

List groups channels under collapsible FNEs and zones. An FNE with only one zone
omits the redundant zone heading. Wide iPad windows use multiple columns. Tap an
FNE connection button to connect or disconnect it. The console status shows
receiving channels; tapping it reveals the first receiving channel. To reorder
rows, long-press a non-button area of a row and drop it onto another row in the
same zone. The moved row takes that position and the surrounding rows shift.

In iPad Cards, choose the FNE and zone tabs to browse channels. Codeplug colors
identify the tabs, and green activity markers show reception. Connection pills
in the title row scroll horizontally. Long-press a card's title area, then drag
to any position on the zone grid, including empty space. Cards snap to the grid
without moving their neighbors, so gaps remain where you leave them. Scroll the
surface horizontally or vertically to reach other positions. SECURE stays in the
header; operational buttons remain at the bottom.

The configuration supplies the initial card arrangement. Your card positions and
List order are saved independently for each configuration on this device.

Live audio and system playback controls stay available while an FNE connection or
web stream is active. Disconnecting the last FNE and stopping all web streams
clears the live player and releases idle audio. Disconnect FNEs individually or
use **Settings → Connections → Disconnect systems**; stop web streams under
**Settings → Web Streams**. Saved recordings can still be played while disconnected.
Playback starts audio as needed and releases it again when the recording ends
or is stopped, unless an FNE connection or web stream still needs it.

## Configuration and recording files

Use **Settings → Configuration Library → Import from Files** to import a YAML
configuration and its companion files, or a configuration ZIP bundle. Imported
content is copied into the app's storage. Editing the original file in Files does
not change the running configuration; import the new version through the Library
when you want to use it.

### Transfer a configuration through iCloud Drive

On a saved configuration, choose **Save bundle to Files…**, confirm inclusion of
passwords and encryption keys, and select a location in **iCloud Drive**. The ZIP
contains the saved configuration and its available companion files. Keep it
private. For a copy without secrets, use **Export sanitized YAML** instead;
that export excludes companion files.

On another iPhone or iPad signed in to the same iCloud account, choose
**Import from Files** and select the ZIP after it becomes available in Files.
There is no need to unzip it. Review any validation warnings or conflict choices,
then open the saved configuration when ready to activate it. Importing alone
does not replace the running session. Conflicting imports can be saved separately
or replace the saved copy as a new revision; previous revisions remain local.

On desktop, **Configuration Library → Export Bundle…** creates the same ZIP
format. Save it to iCloud Drive or transfer it to Files on the receiving device.
Bundles do not transfer device-local operator settings, layout choices or recordings.

iCloud Drive handles file availability; Console NEO keeps an independent local
copy for offline operation. If iCloud Drive is unavailable, use another Files
location and transfer the bundle later. Export again after saving further edits.

Open History to review call details and play recordings. Use its export controls
to save recordings or history to Files. Clearing session history retains TAR
recordings; deleting an archive recording is a separate action.

## Manual PTT and microphone processing

Under **Settings → Push to Talk**, **Tap to toggle PTT** chooses between holding
to talk and tapping once to start, again to stop. The choice is saved per configuration on this
device. Changing it releases manual transmission first.

**Talk-permit tone** and **Mute RX audio while transmitting** control manual-call
audio. **Settings → Audio → Microphone Processing** provides mic gain,
low/mid/high EQ and optional automatic gain control with a target level. Mic gain
runs from −12 to +12 dB with 0 dB (1×) at the center. Gain, EQ and AGC target use
0.25 dB steps and display one decimal place. Select **Apply** to save the processing
values; **Reset fields to defaults** fills the editor without saving until Apply.
These audio settings are device-local and take effect on the next manual call.
They do not change the system microphone route or enable Apple voice processing.

The processing page also saves named gain/EQ presets. **Load preset** fills the
editor; **Apply** changes the next call. AGC stays independent. Saving an existing
name replaces that preset. **Delete preset** leaves applied processing unchanged,
and **Undo preset deletion** restores the last deleted preset while the page is
open.

## Receive processing

Use **Settings → Audio → Receive Processing** to choose a protocol and adjust its
high-pass filter, peaking filter and compressor. Each protocol has separate saved
values. **Apply receive processing** saves the selected profile and restarts
listening decoders, which may briefly interrupt local audio. Listening selections,
TAR selection and output mute remain in place. TAR recordings and outbound patches
bypass the optional RX high-pass filter, peaking filter and compressor; they
retain the normal decoder and receive-buffering behavior.
If audio is paused by iOS, the saved settings apply when listening resumes.

## Audio and interruptions

All channels share the system output route, with individual volume and balance.
Use **Settings → Audio → Choose system output** to open the system route picker. Desktop output
device assignments remain in imported settings, but iOS does not provide
independent physical outputs for each channel.

Listening does not require microphone permission. Microphone transmission needs
permission and a usable input route. Headset loss releases manual transmission
before another microphone can be used. Use **Settings → Audio → Choose microphone** to
request access and list available inputs. This pauses audio without starting
capture. Select the input, then choose **Resume listening**. A new PTT action is
still required to transmit. If the selected input disappears, choose another
input or **System default** explicitly. The input choice is saved for this configuration on this device. A saved
microphone that is disconnected remains selected and is shown as unavailable.

Locking the screen releases manual PTT and cancels tones. Listening, recording,
web playback and enabled automatic patches can continue only while iOS permits
audio execution. Interruptions stop active transmission; recovery does not replay
interrupted calls. Returning to the app retries paused listening; the console
speaker control can also resume it. If recovery fails, open
**Settings → Connections** and use **Resume listening** to retry after correcting
the reported problem. Resuming listening does not restart an interrupted transmission.

## Connections and history

**Settings → Connections** contains startup connection and receive-selection
preferences, along with **Receive Buffering** for per-FNE P25, DMR and NXDN
adaptive or fixed buffering. Buffer changes apply to new calls.

**Settings → History** combines session calls and retained recordings. Expand
Advanced Filters to narrow results. **Settings → Retention** reviews which
recordings a retention change would delete before you apply it.

## Tools

- **Web streams:** start or stop a configured stream and set its volume.
- **Groups and patches:** review membership, one-way sources and enabled
  patches. For a multi-select group, save any membership edits, then choose
  **Add saved group to TX selection**. Return to Console and use **TX selected**
  to transmit. Existing TX selections remain selected; selecting the group does
  not start PTT.
- **Subscriber commands:** send Page, Radio Check, Inhibit or Uninhibit to a
  decimal P25 subscriber RID. Inhibit and Uninhibit require confirmation. A sent
  result means the command was submitted; subscriber acknowledgement decoding is
  not yet available.
- **Logs:** open **Settings → Diagnostics → Logs** to search and export session
  diagnostics.
- **Engineering health:** open **Settings → Diagnostics → Engineering Health**
  to inspect queue, microphone, recording and recovery measurements. Use these
  details to investigate a problem without crowding the main console.
- **Help:** search this bundled guide without an internet connection. Links to
  external websites require connectivity.

Serial and global-keyboard PTT, floating windows and always-on-top are desktop
facilities. Mobile uses the touch controls and system-managed audio route.
