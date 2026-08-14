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

The first implementation targets the console voice format: 8 kHz, mono,
16-bit PCM. The native stream uses the device's nominal CoreAudio rate and the
managed adapter converts to or from the requested voice rate, so common 48 kHz
devices can feed the 8 kHz vocoder boundary. macOS may request microphone
permission when capture is first used.

The Windows path is kept behind the same contracts through
`WindowsAudioBackend`, using NAudio's WinMM event adapters for input and
output. `AudioBackendFactory.CreateDefault()` selects CoreAudio on macOS and
the NAudio backend on Windows; no Windows audio code is loaded on macOS.
