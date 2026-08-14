# Desktop publishing

The rebuild currently publishes framework-dependent Avalonia desktop outputs
for Apple Silicon macOS and Windows x64. The native macOS audio shim is built
and copied beside the managed output by the publishing script.

From the repository root:

```sh
scripts/publish-desktop.sh osx-arm64 /tmp/dvmconsole-osx-arm64
scripts/publish-desktop.sh win-x64 /tmp/dvmconsole-win-x64
```

The macOS output still needs the external `libvocoder.dylib` from the
`dvmvocoder` build described in `docs/VOCODER.md`. Code signing, notarization,
installer packaging, and a Windows hardware/audio run remain handoff work.
