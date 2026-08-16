# macOS audio backend

The rebuild's macOS audio implementation uses a small native CoreAudio/AudioUnit
shim. The shim exposes device enumeration and mono signed 16-bit PCM streams to
the managed `DvmConsole.Audio` layer. It is deliberately separate from the
cross-platform audio contracts so a Windows WASAPI/NAudio implementation can be
added without changing the application core.

Build the native library on Apple Silicon:

```sh
cmake -S native/dvmaudio -B /tmp/dvmaudio-build -DCMAKE_BUILD_TYPE=Release
cmake --build /tmp/dvmaudio-build --config Release
```

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
devices can feed the 8 kHz vocoder boundary. macOS may request microphone
permission when capture is first used.

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

The macOS backend is intentionally not treated as an iOS/iPadOS backend. A
mobile host needs an `AVAudioSession` configured for simultaneous input/output,
record permission with `NSMicrophoneUsageDescription`, and interruption, route
change, and media-services-reset handling. For two-way dispatch voice, start
with the `playAndRecord` category and evaluate `voiceChat` plus either the Voice
Processing I/O Audio Unit or `AVAudioEngine.setVoiceProcessingEnabled`; those
paths provide system echo cancellation and automatic gain control. Whether
voice processing should be enabled must remain an operator/product choice,
because the console already offers its own input gain, AGC, and EQ and must not
apply both processing chains accidentally.

Apple references used for this audit:

- [Audio Components](https://developer.apple.com/documentation/audiotoolbox/audio-components)
- [Migrating an Audio Unit host to AUv3](https://developer.apple.com/documentation/audiotoolbox/migrating-your-audio-unit-host-to-the-auv3-api)
- [Understanding Audio Workgroups](https://developer.apple.com/documentation/audiotoolbox/understanding-audio-workgroups)
- [AVAudioSession](https://developer.apple.com/documentation/avfaudio/avaudiosession)
- [`playAndRecord`](https://developer.apple.com/documentation/avfaudio/avaudiosession/category-swift.struct/playandrecord)
- [`voiceChat`](https://developer.apple.com/documentation/avfaudio/avaudiosession/mode-swift.struct/voicechat)
- [Requesting record permission](https://developer.apple.com/documentation/avfaudio/avaudioapplication/requestrecordpermission(completionhandler:))
- [Responding to audio route changes](https://developer.apple.com/documentation/avfaudio/responding-to-audio-route-changes)

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
