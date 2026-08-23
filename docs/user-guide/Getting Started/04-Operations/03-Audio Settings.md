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

DVM Console provides mutually exclusive microphone processing modes:

- **DVM Console processing** applies the console gain, equalizer, and optional
  automatic gain control after capture.
- **Apple voice processing** uses Apple's full-duplex Voice Processing I/O for
  acoustic echo cancellation and automatic gain control. DVM Console gain,
  equalizer, and AGC are bypassed in this mode so the signal is not processed
  twice.

- **Windows communications processing** requests the communications effects
  supplied by Windows, the selected audio driver, and the endpoint. Depending on
  that combination, the effects can include acoustic echo cancellation, noise
  suppression, and automatic gain control. DVM Console gain, equalizer, and AGC
  are bypassed in this mode so the signal is not processed twice.

Apple voice processing is available only on macOS. Windows communications
processing is available only on Windows. DVM Console processing remains the
default on both platforms. Windows communications effects are device-dependent;
selecting the mode does not guarantee that every effect is provided by a given
endpoint.

Apple voice processing supports the system-default microphone/speaker pair or
one Core Audio device that provides both input and output. macOS does not allow
the Voice Processing I/O unit to use a private aggregate of unrelated selected
devices. Use DVM Console processing when the microphone and speaker are separate
non-default devices.

Applying a different main route or processing mode automatically restarts every
active listening channel. The operator does not need to turn each card off and
on manually.

The **Automatic gain control** checkbox controls the DVM Console microphone AGC
path.

When enabled, the console applies its microphone AGC path before transmit.

When disabled, the console uses the raw microphone behavior.

This setting is saved.

---

# Mute RX Audio While Transmitting

This setting is in the Settings menu, not the Audio Settings window:

```
Settings > Mute RX Audio While Transmitting
```

When enabled, local RX speaker playback is suppressed while the console is transmitting. In Apple voice-processing mode the mixer is silenced in place so the full-duplex unit and macOS microphone-mode state remain active.

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
- If Apple voice processing reports an incompatible split-device route, choose
  the system-default pair, a single duplex device, or DVM Console processing.
