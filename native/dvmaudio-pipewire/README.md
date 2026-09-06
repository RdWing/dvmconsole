# Linux PipeWire audio shim

This library is the native audio boundary for the Linux desktop host. It uses
PipeWire's native stream API for capture and playback and exports the same
small PCM-oriented C ABI consumed by the managed platform adapter.

The shim always exposes the session manager's default input and output routes.
It also snapshots physical capture and playback nodes from the PipeWire
registry, uses each node's `object.serial` as the stable application device ID,
and marks BlueZ-backed devices as Bluetooth. Opening a physical device targets
that PipeWire object; opening device ID zero continues to follow the system
default route. A native registry and metadata monitor reports physical-device
and default-route changes immediately, with managed polling retained as a
recovery fallback.

## Build

Install a C11 compiler, CMake, `pkg-config`, and the development files for
`libpipewire-0.3`, then run:

```sh
cmake -S native/dvmaudio-pipewire -B native/dvmaudio-pipewire/build
cmake --build native/dvmaudio-pipewire/build
ctest --test-dir native/dvmaudio-pipewire/build --output-on-failure
```

The resulting library is `libdvmaudio-pipewire.so`.

The portable preview-package path builds the shim against Debian 12, runs its
shared PCM-ring test, enforces the GLIBC 2.34 ceiling, and places the library
beside the managed apphost:

```sh
scripts/build-linux-portable.sh linux-x64 artifacts DVMConsole-x86_64.AppImage
```

Use the matching command on an ARM64 Linux host for `linux-arm64`. Cross-builds
must provide `DVM_LINUX_CMAKE_TOOLCHAIN` and a matching Rust linker; the publish
verifier rejects native libraries whose ELF architecture differs from the RID.
The resulting AppImage is one executable file containing the verified publish
tree and the application-owned runtime files.

## PulseAudio compatibility

DVM Console connects to PipeWire directly; it does not load `libpulse` and
does not fall back to a separate PulseAudio daemon. On distributions that use
`pipewire-pulse`, PulseAudio applications and DVM Console share the same
PipeWire graph and session-manager routes. A host running only a legacy
PulseAudio daemon will require PipeWire (and its normal session manager) before
the Linux desktop host can provide audio.

This keeps one routing and device model in the application while retaining
compatibility with other software that still uses the PulseAudio client API.
