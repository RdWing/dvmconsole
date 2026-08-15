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

The first implementation targets the console voice format: 8 kHz, mono,
16-bit PCM. The native stream uses the device's nominal CoreAudio rate and the
managed adapter converts to or from the requested voice rate, so common 48 kHz
devices can feed the 8 kHz vocoder boundary. macOS may request microphone
permission when capture is first used.

The Windows path is kept behind the same contracts through
`WindowsAudioBackend`, using NAudio's WinMM event adapters for input and
output. `AudioBackendFactory.CreateDefault()` selects CoreAudio on macOS and
the NAudio backend on Windows; no Windows audio code is loaded on macOS.

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

The Avalonia host enables the adapter when `DVM_PTT_SERIAL_PORT` is set and
accepts an optional positive `DVM_PTT_SERIAL_BAUD` value (default `9600`).
Keyboard and serial sources are combined fail-safe: releasing either source
does not stop an active call while the other source remains pressed.
