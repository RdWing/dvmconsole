# macOS audio backend

The rebuild's macOS audio implementation uses a small native CoreAudio/AudioUnit
shim. The shim exposes device enumeration and mono signed 16-bit PCM streams to
the managed `DvmConsole.Audio` layer. It is deliberately separate from the
cross-platform audio contracts so a Windows WASAPI/NAudio implementation can be
added without changing the application core.

Build the native library for the host Mac:

```sh
cmake -S native/dvmaudio -B /tmp/dvmaudio-build -DCMAKE_BUILD_TYPE=Release
cmake --build /tmp/dvmaudio-build --config Release
```

To cross-build for the other Mac architecture, add
`-DCMAKE_OSX_ARCHITECTURES=arm64` or
`-DCMAKE_OSX_ARCHITECTURES=x86_64` to the configure command. Release builds
also use `-DCMAKE_OSX_DEPLOYMENT_TARGET=14.0`, matching the .NET 10 support
floor and application bundle metadata. The desktop publisher supplies the
architecture and deployment target automatically.

Then enumerate devices:

```sh
DVM_AUDIO_LIBRARY=/tmp/dvmaudio-build/libdvmaudio.dylib \
  dotnet run --project src/DvmConsole.AudioProbe/DvmConsole.AudioProbe.csproj --no-restore
```

Exercise the default input and output streams for two seconds:

```sh
DVM_AUDIO_LIBRARY=/tmp/dvmaudio-build/libdvmaudio.dylib \
  dotnet run --project src/DvmConsole.AudioProbe/DvmConsole.AudioProbe.csproj --no-restore -- \
  --stream-test 2
```

Exercise the full-duplex Apple Voice Processing I/O path (AEC and Apple AGC)
using the system default input/output pair:

```sh
DVM_AUDIO_LIBRARY=/tmp/dvmaudio-build/libdvmaudio.dylib \
  dotnet run --project src/DvmConsole.AudioProbe/DvmConsole.AudioProbe.csproj --no-restore -- \
  --voice-processing-stream-test 2
```

Exercise the talk-permit tone path and wait for queued playback to drain. Pass
an optional CoreAudio output device ID to select a specific output:

```sh
DVM_AUDIO_LIBRARY=/tmp/dvmaudio-build/libdvmaudio.dylib \
  dotnet run --project src/DvmConsole.AudioProbe/DvmConsole.AudioProbe.csproj --no-restore -- \
  --permit-tone [device-id]
```

The probe reports the resolved output device plus queued and consumed sample
counts. The desktop menu uses the same drain contract for the local permit
tone.

Check the macOS global-keyboard permission and lifecycle without starting the
desktop shell:

```sh
dotnet run --project src/DvmConsole.AudioProbe/DvmConsole.AudioProbe.csproj --no-restore -- \
  --global-ptt F12
```

The probe reports the permission/startup error directly when the terminal or
packaged application has not been granted Accessibility or Input Monitoring.

The first implementation targets the console voice format: 8 kHz, mono,
16-bit PCM. The native stream uses the device's nominal CoreAudio rate and the
managed adapter converts to or from the requested voice rate, so common 48 kHz
devices can feed the 8 kHz transmit path. macOS may request microphone
permission when capture is first used.

## High-quality AirPods audio

On macOS 26 and later, Audio Settings exposes **Use high-quality AirPods audio
when supported**. When enabled, the native shim applies the full-bandwidth
Bluetooth recording method demonstrated by the MIT-licensed BetterMic project:
it dynamically configures the otherwise macOS-unavailable `AVAudioSession`
runtime with `playAndRecord`, default mode, Bluetooth HFP fallback, and the
high-quality Bluetooth recording option before CoreAudio opens the microphone.
As in BetterMic, a silent `AVAudioEngine` input tap holds that Bluetooth mode
while DVM Console's existing CoreAudio stream supplies the real transmit audio.

The feature fails open to normal CoreAudio. It is attempted only when both the
system-default input and output devices use the Bluetooth transport. When the
runtime exposes Apple's Bluetooth microphone capability object, the shim checks
`highQualityRecording.isSupported` and later `isEnabled`; it also confirms that
both active devices reached at least 44.1 kHz. Older macOS versions, unsupported
AirPods, unavailable regions, split/non-default routes, and runtime API failures
continue through the existing Bluetooth/HFP path without preventing capture.

Apple notes that high-quality Bluetooth recording can increase input latency.
Operators can disable it independently from DVM Console processing and Apple
Voice Processing. Keeping the transmit microphone warm can avoid paying the
Bluetooth route-switch delay on each PTT press, at the cost of holding the
microphone route open.

The Windows path is kept behind the same contracts through
`WindowsAudioBackend`, using NAudio's WinMM event adapters for input and
output. `AudioBackendFactory.CreateDefault()` selects CoreAudio on macOS and
the NAudio backend on Windows; no Windows audio code is loaded on macOS.

## Apple API and real-time design audit

The native macOS shim follows Apple's Audio Component loading model. It finds
Apple's HAL Output Audio Unit with `AudioComponentFindNext`, creates it with
`AudioComponentInstanceNew`, configures its stream format and render callback,
and starts it with `AudioOutputUnitStart`. This is an Audio Unit v2 system-I/O
host, not a third-party plug-in host. AUv3 migration is therefore not required
for the current macOS hardware path; if the console later hosts effects or
other third-party Audio Units, use `AVAudioUnitComponentManager` and
`AVAudioUnit.instantiate` so AUv2 and AUv3 components share Apple's bridging
layer.

Both hardware callbacks are kept bounded and real-time-safe: their storage is
allocated before the Audio Unit starts, the callbacks use only the preallocated
PCM buffer and lock-free ring, and managed decoding, rate conversion, network
I/O, logging, and UI work remain off the render thread. The framework-provided
render thread is automatically joined to the device's audio workgroup; the shim
does not create an auxiliary real-time thread, so it does not need to join one
manually.

The desktop exposes two mutually exclusive policies: **DVM Console
processing** and **Apple voice processing**. The Apple policy uses one
full-duplex Voice Processing I/O Audio Unit for the compatible input/output devices selected
in Audio Settings, enables Apple's AEC and AGC, and bypasses DVM Console's microphone gain,
EQ, and AGC so the chains cannot be applied twice. The existing per-channel
`AudioMixer` remains upstream and sends one final mixed radio signal to the
unit, allowing simultaneous radio channels to share the same echo reference.
On macOS, Voice Processing I/O supports the system-default input/output pair or
one selected duplex Core Audio device. Core Audio rejects a private aggregate as
the unit's current device, so selecting two unrelated non-default devices is
reported as incompatible instead of repeatedly rebuilding the system microphone
route. DVM Console processing remains available for split-device routing.
Per-channel routes inherit the main output by default. Explicit alternate
physical outputs remain available through HAL but are outside the Apple AEC
reference.

Changing the main route or processing mode stops and restarts every active
listening channel automatically. In Apple mode the duplex unit remains alive
while PTT is held, even when the general "Mute RX while transmitting" preference
is enabled; the active mixer channels are silenced in place and their configured
levels are restored on release. This avoids repeatedly removing and recreating
macOS microphone-mode state during a call without changing the mute preference's
operator-visible behavior.

The policy enum and backend selection are platform-neutral plumbing intended
for reuse by an iOS/iPadOS host. That mobile backend will additionally need an
`AVAudioSession` configured with `playAndRecord`, likely `voiceChat`, record
permission with `NSMicrophoneUsageDescription`, and interruption, route-change,
and media-services-reset handling. On iOS/iPadOS 18 and later,
`NSAlwaysAllowMicrophoneModeControl` can additionally expose microphone-mode
selection before capture becomes active. The public macOS SDK marks
`AVAudioSession` unavailable. The optional macOS 26 high-quality AirPods path
therefore performs guarded runtime lookup, while the normal macOS implementation
continues to use Voice Processing I/O directly.

Apple references used for this audit:

- [Audio Unit Voice I/O](https://developer.apple.com/documentation/audiotoolbox/audio-unit-voice-i-o)
- [Audio Components](https://developer.apple.com/documentation/audiotoolbox/audio-components)
- [Migrating an Audio Unit host to AUv3](https://developer.apple.com/documentation/audiotoolbox/migrating-your-audio-unit-host-to-the-auv3-api)
- [NSAlwaysAllowMicrophoneModeControl](https://developer.apple.com/documentation/bundleresources/information-property-list/nsalwaysallowmicrophonemodecontrol)
- [Understanding Audio Workgroups](https://developer.apple.com/documentation/audiotoolbox/understanding-audio-workgroups)
- [AVAudioSession](https://developer.apple.com/documentation/avfaudio/avaudiosession)
- [`playAndRecord`](https://developer.apple.com/documentation/avfaudio/avaudiosession/category-swift.struct/playandrecord)
- [`voiceChat`](https://developer.apple.com/documentation/avfaudio/avaudiosession/mode-swift.struct/voicechat)
- [Requesting record permission](https://developer.apple.com/documentation/avfaudio/avaudioapplication/requestrecordpermission(completionhandler:))
- [Responding to audio route changes](https://developer.apple.com/documentation/avfaudio/responding-to-audio-route-changes)
- [Bluetooth high-quality recording](https://developer.apple.com/documentation/avfaudio/avaudiosession/categoryoptions-swift.struct/bluetoothhighqualityrecording)
- [BetterMic reference implementation](https://github.com/ygzo/bettermic)

## Global keyboard PTT

On macOS, the configured Space/F-key PTT can use a listen-only CoreGraphics
event tap so it remains active when another application has focus. macOS must
grant DVM Console Accessibility or Input Monitoring permission; if the event
tap cannot be created, the desktop host reports the reason and keeps the
focused-window keyboard PTT path available. Windows uses a low-level,
non-swallowing keyboard hook with the same fallback behavior. Both native
adapters release their event loop or hook during stop and window shutdown.

## Hardware PTT

`SerialPttSource` provides a cross-platform adapter for USB serial
footswitches and small controllers. Configure the device to emit one line per
state change using `on`/`1`/`pressed` for transmit and `off`/`0`/`released` for
receive. The adapter releases PTT on EOF, stop, or a read fault. Its stream
factory overload is available for host-specific serial transports and tests;
the direct constructor uses `System.IO.Ports` on both macOS and Windows.

Configure the adapter from **Settings → Global PTT settings… → PTT** in Console
Tools. The device list can be refreshed while the application is running; the
selected port, baud rate, and enabled state are persisted in the user settings,
and Apply safely releases and replaces the previous source. `DVM_PTT_SERIAL_PORT`
and the optional `DVM_PTT_SERIAL_BAUD` (default `9600`) remain backward-compatible
fallbacks only when no serial device has been saved through the GUI.
Keyboard and serial sources are combined fail-safe: releasing either source
does not stop an active call while the other source remains pressed.
