# Audio Settings

Open Audio Settings from:

```
Audio > Audio settings
```

Audio Settings controls microphone input and local speaker output routing.

---

# Global Input Device

The global input device is the microphone used for console transmit audio.

Options include:

- System Default Input
- specific installed input devices

If System Default Input is selected, the console follows the current macOS or Windows default microphone.

If a specific device is selected, the console uses that device instead.

If a saved device is missing, the console falls back to the system default and reports the change in the audio status.

On macOS, use **Request macOS microphone access** beside **Refresh devices** and
**Test talk permit tone** to ask for capture permission. If access was previously
denied, enable DVM Console under **System Settings > Privacy & Security >
Microphone**.

---

# Device Persistence

Audio device selections are saved by device identity, not only by a temporary device number.

This helps prevent routing from jumping to the wrong microphone or speaker when USB audio devices are plugged in, unplugged, or reordered by Windows.

If a saved device is temporarily unavailable, the console uses the system default until that device returns.

---

# Master Output Device

The master output device is the default speaker/output device for resources.

Options include:

- System Default Output
- specific installed output devices

If System Default Output is selected, the console follows the current macOS or Windows default playback device.

The speaker button beside Warm mic in the main toolbar mutes live RX presentation
to configured output devices. Muting does not stop receive decoding, call state,
patching, or TAR recording, and the mute state resets when the console restarts.

---

# RX Audio Processing

The **RX audio processing options** table configures the built-in decoder's
optional post-processing independently for P25 Phase 1, P25 Phase 2, DMR, and
NXDN. These options affect receive playback and patch-source decoding only;
microphone capture and the transmitted vocoder signal are unchanged.

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

# Per-Resource Output Overrides

Audio Settings is organized by the same zones/tabs used on the main console.

Each channel resource and web stream chip can use:

```
Default (Master Output)
```

or a specific output device override.

Use default for most resources. Use overrides when a specific talkgroup must always play through a different speaker or audio interface.

Web stream output overrides, volume, and position are keyed by stream name. Automatic startup is more restrictive: it is bound to the codeplug path, canonical URL, and configured credentials that the operator previously started. Keep the name and stream definition stable if you want all saved behavior to continue applying.

---

# Microphone Processing

DVM Console processing is the standard microphone path on macOS. It applies
the console gain, equalizer, and optional automatic gain control after capture.
Apple Voice Processing is no longer available: the optimized normal
Core Audio path does not require the additional full-duplex Apple processing
route. A saved Apple processing selection from an earlier release is changed to
DVM Console processing the next time the application starts.

On Windows, the mode selector also offers **Windows communications processing**.
That mode requests the communications effects supplied by Windows, the selected
audio driver, and the endpoint. Depending on that combination, the effects can
include acoustic echo cancellation, noise suppression, and automatic gain
control. DVM Console gain, equalizer, and AGC are bypassed so the signal is not
processed twice. These endpoint-provided
effects remain device-dependent and are not guaranteed by selecting the mode.

Applying a different main input or output route—and, on Windows, a different
processing mode—automatically restarts every active listening channel and web
stream around the route change. Active recording-file playback stops rather
than carrying an obsolete backend into the new route. The operator does not
need to turn each channel card off and on manually. Stop transmitting before
applying a route or processing-mode change.

The **Automatic gain control** checkbox controls the DVM Console microphone AGC
path.

When enabled, the console applies its microphone AGC path before transmit.

When disabled, the console uses the raw microphone behavior.

This setting is saved.

## Bluetooth PTT timing

Bluetooth headsets still need time to change into their microphone-capable
duplex profile when a cold PTT begins. DVM Console waits for the first non-empty
selected-microphone callback after the route transition and then completes the
talk-permit cue. On macOS, the cue path also accounts for the output device's
reported presentation latency. Microphone audio remains blocked until the cue
path completes; if it cannot complete, the new PTT call stops without releasing
operator audio.

The standard DVM Console processing path avoids the additional shared
full-duplex coordination formerly required by Apple Voice Processing. In live
testing this reduced Bluetooth PTT startup delay. Exact
timing remains dependent on the headset, macOS, the current profile, and whether
the microphone was already warm. Improvements throughout the audio chain mean
most headsets should not require **Keep transmit microphone warm**.

Leave **Keep transmit microphone warm** off for most headsets. Enable it only
when a particular device still has unacceptable repeated cold-start delay and
that benefit matters more than retaining the headset's higher-quality
playback-only profile while idle.

---

# Mute RX Audio While Transmitting

This setting is in the Settings menu, not the Audio Settings window:

```
Settings > Mute RX Audio While Transmitting
```

When enabled, local RX speaker playback is suppressed while the console is transmitting.

This does not affect:

- received network traffic
- logs
- RX card visual state
- patch forwarding
- transmit audio

It only mutes local playback while TX is active.

---

# Stale Routing Cleanup

When a codeplug is loaded, the console prunes saved per-resource settings that clearly refer to resources or talkgroups no longer present in the loaded codeplug.

This helps keep the AppData settings JSON from accumulating stale audio routing, volume, and position entries.

---

# Tips

- Use system default devices unless a deployment needs fixed hardware routing.
- Use the master output for the normal speaker path.
- Use per-resource overrides sparingly so future troubleshooting is easier.
- If audio is playing from the wrong device, check both the master output and the resource override.
- For lower Bluetooth cold-start delay, use the standard DVM Console processing
  path and consider keeping the transmit microphone warm.
