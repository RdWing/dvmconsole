# Audio settings

Open Audio Settings from:

```
Audio > Audio settings
```

Audio Settings controls microphone input and local speaker routing.

---

# Global input device

The global input device supplies console transmit audio.

Options include:

- System Default Input
- specific installed input devices

With System Default Input selected, DVM Console follows the current macOS or
Windows default microphone.

Selecting a specific device pins capture to that microphone.

If a saved device is missing, DVM Console uses the system default and reports
the change in Audio status.

Choose **Apply** after changing a microphone, output route, or processing value.
DVM Console applies the route as one transaction. If it fails, the previous
devices, processing options, and Keep Mic Warm state are restored, and the
saved configuration is left unchanged. Audio status and Debug Logs report the
failure.

On macOS, use **Request macOS microphone access** beside **Refresh devices** and
**Test talk permit tone** to ask for capture permission. If access was previously
denied, enable DVM Console under **System Settings > Privacy & Security >
Microphone**.

---

# Device persistence

DVM Console saves audio selections by device identity rather than a temporary
device number.

This prevents routes from moving to the wrong microphone or speaker when USB
devices are connected, disconnected, or reordered by Windows.

If a saved device is temporarily unavailable, DVM Console uses the system
default until it returns.

---

# Master output device

The master output is the default speaker route for resources.

Options include:

- System Default Output
- specific installed output devices

If System Default Output is selected, the console follows the current macOS or Windows default playback device.

The three speaker controls beside Keep Mic Warm mute live RX presentation for
the selected system, selected zone, or all configured output devices. Muting
does not stop receive decoding, call state, patching, or TAR recording, and the
mute state resets when the console restarts.

---

# RX audio processing

The **RX audio processing options** table configures decoder post-processing
separately for P25 Phase 1, P25 Phase 2, DMR, and NXDN. These settings affect
receive playback and patch-source decoding. They do not change microphone
capture or the transmitted vocoder signal.

Each mode has independent controls for:

- A high-pass filter, enabled by default at 250 Hz. Its cutoff is selectable
  from 0 to 500 Hz in 25 Hz steps.
- A peaking filter, enabled by default at 2.5 kHz and +3 dB. Its center
  frequency is selectable from 250 Hz to 3 kHz in 25 Hz steps, and its gain is
  bounded from -10 dB to +10 dB.
- A soft-knee compressor, disabled by default. When enabled it defaults to a
  3:1 ratio, -18 dBFS threshold, and +3 dB makeup gain. Ratio is bounded from
  1:1 to 10:1, threshold from -40 dBFS to 0 dBFS, and makeup gain from 0 dB to
  +10 dB. Attack and release remain fixed at 10 ms and 250 ms.

Choose **Apply RX options** to save the table and safely recreate active
listening and patch-source decode sessions; channels do not need to be toggled
manually.

---

# Per-resource output overrides

Audio Settings is organized by the same zones/tabs used on the main console.

Each channel resource and web stream chip can use:

```
Default (Master Output)
```

or a specific output device override.

Use the default route for most resources. Set an override when a talkgroup must
always use another speaker or audio interface.

Web stream output overrides, volume, and position are keyed by stream name.
Automatic startup also checks the managed configuration, canonical URL, and
credentials used when the operator started the stream. Keep the name and
definition stable to retain all saved behavior.

---

# Microphone processing

DVM Console processing is the standard microphone path on macOS. After capture,
it applies console gain, equalization, and optional automatic gain control.
Apple Voice Processing is no longer available because the normal Core Audio
path does not need its additional full-duplex route. At startup, DVM Console
changes any saved Apple processing selection to DVM Console processing.

On Windows, the mode selector also offers **Windows communications processing**.
That mode requests the communications effects supplied by Windows, the selected
driver, and the endpoint. Depending on the combination, these may include
acoustic echo cancellation, noise suppression, and automatic gain control. DVM
Console bypasses its gain, equalizer, and AGC to avoid processing the signal
twice. Endpoint effects depend on the device and are not guaranteed when the
mode is selected.

Applying a different main input or output route, or a different Windows
processing mode, restarts active listening channels and web streams around the
change. Active recording playback stops so it does not keep the old audio
backend. You do not need to cycle each channel card manually. Stop transmitting
before applying a route or processing-mode change.

The **Automatic gain control** checkbox controls the DVM Console microphone AGC
path.

When enabled, DVM Console applies microphone AGC before transmit. When disabled,
it uses the raw microphone behavior. The setting is saved.

## Bluetooth PTT timing

At the start of a cold PTT, a Bluetooth headset needs time to switch into its
microphone-capable duplex profile. DVM Console waits for the first non-empty
callback from the selected microphone before completing the talk-permit cue. On
macOS, it also accounts for the output device's reported presentation latency.
Microphone audio remains blocked until the cue completes. If it cannot complete,
the PTT call stops without transmitting operator audio.

The standard processing path does not need the shared full-duplex coordination
used by Apple Voice Processing. Live testing showed a shorter Bluetooth PTT
startup delay. Exact timing still depends on the headset, macOS, its current
profile, and whether the microphone is already warm. Most headsets should not
need **Keep transmit microphone warm**.

Leave **Keep transmit microphone warm** off for most headsets. Enable it only
when a particular device still has unacceptable repeated cold-start delay and
that benefit matters more than retaining the headset's higher-quality
playback-only profile while idle.

---

# Muting RX audio while transmitting

This setting is in the Settings menu, not the Audio Settings window:

```
Settings > Mute RX Audio While Transmitting
```

When enabled, DVM Console mutes local RX speaker playback during transmit.

This does not affect:

- received network traffic
- logs
- RX card visual state
- patch forwarding
- transmit audio

It only mutes local playback while TX is active.

---

# Stale routing cleanup

When a codeplug loads, DVM Console removes saved per-resource settings that
clearly refer to resources or talkgroups no longer in that codeplug.

This prevents stale audio routes, volumes, and positions from accumulating in
the AppData settings JSON.

---

# Tips

- Use system default devices unless a deployment needs fixed hardware routing.
- Use the master output for the normal speaker path.
- Use per-resource overrides sparingly so future troubleshooting is easier.
- If audio is playing from the wrong device, check both the master output and the resource override.
- For lower Bluetooth cold-start delay, use the standard DVM Console processing
  path and consider keeping the transmit microphone warm.
