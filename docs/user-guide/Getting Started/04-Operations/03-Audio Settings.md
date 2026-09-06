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

With System Default Input selected, DVM Console follows the current operating
system's default microphone.

Selecting a specific device pins capture to that microphone.

If a saved device is missing, DVM Console uses the system default and reports
the change in Audio status.

Choose **Apply** after changing a microphone, output route, or processing value.
DVM Console applies the changes together. If that fails, the previous
devices, processing options, and Keep Mic Warm state are restored, and the
saved configuration is left unchanged. Audio status and Debug Logs report the
failure.

Apply is disabled while a transmission or another connection change is active.
Edits remain in the window while the button is disabled, and Apply becomes
available as soon as TX or the connection work ends. Applying new gain, EQ, or
AGC values recreates a warm microphone capture so the next transmission uses
the new processing settings.

On macOS, use **Request macOS microphone access** beside **Refresh devices** and
**Test talk permit tone** to ask for capture permission. If access was previously
denied, enable DVM Console under **System Settings > Privacy & Security >
Microphone**.

---

# Device persistence

DVM Console saves audio selections by device identity rather than a temporary
device number.

This prevents routes from moving to the wrong microphone or speaker when USB
devices are connected, disconnected, or reordered by the operating system.

If a saved device is temporarily unavailable, DVM Console uses the system
default until it returns.

---

# Master output device

The master output is the default speaker route for resources.

Options include:

- System Default Output
- specific installed output devices

If System Default Output is selected, the console follows the current operating
system's default playback device.

The three speaker controls beside Keep Mic Warm mute live RX audio for
the selected system, selected zone, or all configured output devices. Muting
does not stop receive decoding, call state, patching, or TAR recording, and the
mute state resets when the console restarts.

---

# RX audio processing

The **RX audio processing options** table configures decoder post-processing
separately for P25 Phase 1, P25 Phase 2, DMR, and NXDN. These settings affect
local receive playback only. TAR recordings and outbound patches bypass these
filters and the compressor, so adjusting your listening audio does not change
recordings or what other radios hear. These controls do not change microphone capture or
microphone-originated transmission.

Each mode has independent controls for:

- **High-pass**, enabled by default at 250 Hz. Its cutoff is selectable
  from 0 to 500 Hz in 25 Hz steps.
- **Voice peaking**, enabled by default at 2.5 kHz and +3 dB. Its center
  frequency is selectable from 250 Hz to 3 kHz in 25 Hz steps, and its gain is
  bounded from -10 dB to +10 dB.
- **Compressor ratio/threshold/makeup**, disabled by default. When enabled it
  defaults to a 3:1 ratio, -18 dBFS threshold, and +3 dB makeup gain. Ratio is
  bounded from 1:1 to 10:1, threshold from -40 dBFS to 0 dBFS, and makeup gain
  from 0 dB to +10 dB. Attack and release remain fixed at 10 ms and 250 ms.

Choose **Apply RX options** to save the table and safely recreate active
local receive sessions; channels do not need to be toggled manually. Active
patch-source decoders keep running without interruption.

DMR, NXDN, and P25 Phase 2 use unity gain after vocoder decode, as does P25
Phase 1. Use the per-channel volume and optional processing controls for local
presentation instead of relying on a protocol-specific fixed boost.

---

# Per-resource output overrides

Audio Settings is organized by the same zones/tabs used on the main console.

Each channel resource and web stream card can use:

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

DVM Console processing is the standard microphone path on macOS and Linux, and
the default path on Windows. After capture, it applies console gain,
equalization, and optional automatic gain control.

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

When enabled, DVM Console adjusts microphone gain automatically before
transmit. Turning AGC off leaves the console gain and equalizer settings in
effect. The setting is saved.

## Talk-permit preparation

PTT starts warming the microphone immediately. With the permit tone enabled,
ordinary output preparation sends 40 ms of silence to prime the speaker path
and verify that playback is consuming audio before the audible cue. It helps
keep the beginning of the tone from being lost while output starts. This is
output preparation, separate from microphone readiness, and can overlap
microphone startup. Microphone audio remains held back until tone presentation
finishes, then waits another 60 ms and for fresh microphone input. These timing
intervals are built in; they are not adjustable settings and do not determine
the total PTT delay on their own.

Use **Test talk permit tone** to check the selected route. With speakers and a
desk microphone, check a short test transmission for acoustic pickup and avoid
speaking during the cue. A headset reduces direct speaker-to-microphone pickup.

## Bluetooth PTT timing

At the start of a cold PTT, a Bluetooth headset needs time to switch into its
microphone-capable duplex profile. DVM Console waits for the first non-empty
callback from the selected microphone before completing the talk-permit cue. On
macOS, it also accounts for the output device's reported presentation latency.
Microphone audio remains blocked until the cue completes. If it cannot complete,
the PTT call stops without transmitting operator audio.

The standard processing path avoids unnecessary full-duplex route coordination.
Startup timing depends on the headset, operating system, current profile, and
whether the microphone is already warm.

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
