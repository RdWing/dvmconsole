# Desktop publishing

The rebuild currently publishes framework-dependent Avalonia desktop outputs
for Apple Silicon macOS and Windows x64. The native macOS audio shim is built
and copied beside the managed output by the publishing script.

From the repository root:

```sh
scripts/publish-desktop.sh osx-arm64 /tmp/dvmconsole-osx-arm64
scripts/publish-desktop.sh win-x64 /tmp/dvmconsole-win-x64
```

Verify each output before handing it off:

```sh
scripts/verify-publish.sh osx-arm64 /tmp/dvmconsole-osx-arm64
scripts/verify-publish.sh win-x64 /tmp/dvmconsole-win-x64
```

The verifier checks the managed runtime payload, the required macOS audio
shim, native-library architecture, and that testing codeplug or credential-like
configuration material was not copied into the publish directory.

The native vocoder is optional at publish time. Set `DVMVOCODER_LIBRARY` to
copy it into the output under the platform name expected by the runtime loader:

```sh
DVMVOCODER_LIBRARY=/path/to/libvocoder.dylib \
  scripts/publish-desktop.sh osx-arm64 /tmp/dvmconsole-osx-arm64
```

Web streams decode WAV and MP3 without an external media process. To enable
additional compressed formats, install a compatible FFmpeg executable on the
target machine and set `DVM_FFMPEG` to its path; FFmpeg is intentionally not
bundled by the unsigned publish script.

Code signing, notarization, installer packaging, and a Windows hardware/audio
run remain handoff work.
