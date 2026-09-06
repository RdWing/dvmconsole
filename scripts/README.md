# Build and validation scripts

Run commands from the repository root. Use Bash on macOS/Linux and PowerShell
on Windows. Python helpers use the standard library. Build prerequisites and
supported targets are in the [build guide](../docs/user-guide/Getting%20Started/02-Building.md).

| Task | Entry points |
|---|---|
| Publish an unpackaged desktop build | `publish-desktop.sh` / `publish-desktop.ps1` |
| Package an existing publish directory | `package-desktop.sh` / `package-desktop.ps1` |
| Build a portable Linux AppImage | `build-linux-portable.sh` |
| Verify an unpackaged build, including native libraries | `verify-publish.sh` / `verify-publish.ps1` |
| Compare a ZIP with its source inventory | `verify-package.py --archive ... --publish-root ... --rid ...` |
| Check common publish files and documentation | `verify-package.py --publish-only --publish-root ... --rid ...` |
| Check archive size | `verify-package-size.py` |
| Smoke-test a macOS app or Linux AppImage | `smoke-desktop-macos.sh` / `smoke-desktop-linux-appimage.sh` |
| Test Linux distribution compatibility | `smoke-desktop-linux-container.sh` |
| Probe the native PipeWire library | `probe-linux-pipewire.sh` |

The native publish verifiers call the common Python inventory check; the
Python check alone does not verify binary architecture or native dependencies.
Publish and package commands validate their own inputs so they can be used
independently. Verification after extraction checks the delivered artifact,
not just the directory it was built from.

`package-desktop-linux-appimage.sh`, `create-reproducible-zip.py`,
`validate-package-target.py`, and `invoke-smoke-with-timeout.ps1` support those
entry points. `release_metadata.py` handles release names, notes, checksums and
SBOM metadata. Keep publication separate from local build commands.

Run every helper test with:

```sh
python3 -m unittest discover -s scripts/tests -p 'test_*.py'
```

For source checks:

```sh
scripts/verify-source-headers.sh
scripts/verify-public-privacy.sh
python3 scripts/verify-documentation.py --root docs/user-guide
scripts/verify-format.sh
```

For an affected managed layer, run its test project in Release configuration.
Before integration, run the complete solution:

```sh
dotnet restore dvmconsole.sln --locked-mode -p:Configuration=Release
dotnet test dvmconsole.sln --no-restore --configuration Release
```

CI runs shared header/privacy/documentation checks once, formatting on one
matrix target, and all helper and solution tests on each of the six targets.
Native tests, package smoke, reproducibility, checksums and release read-back
remain separate gates. A passing unit suite does not replace device or radio
acceptance.
