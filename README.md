# DVM Console

DVM Console is a cross-platform desktop dispatch application for monitoring
multiple talkgroups across one or more DVM FNE systems. The standalone Avalonia
application supports Apple Silicon and Intel macOS plus Windows x64.

Release packages are self-contained for all supported platforms.

![DVM Console Neo dark theme](./repo/neo-dark.png)

## Compatibility Warning

DVMConsole R02A00 has limited backwards compatibility with older FNE builds and older codeplugs.

DVMConsole R02A00 is intended for use with DVMHost/FNE R06A00 or newer.

Older FNE builds are not recommended and may behave unpredictably with this console release.

Codeplugs created for R01A00 should be reviewed before use with R02A00. There have been major changes to resource configuration.

## Building DVM Console

This project utilizes the Avalonia desktop framework for the Apple Silicon
macOS, Intel macOS, and Windows x64 builds. A .NET 10 SDK installation, Rust
1.85 or newer, CMake, and a platform C/C++ toolchain are required to compile
the application.

### Dependencies

- .NET 10 SDK
- Rust 1.85 or newer
- CMake
- A platform C/C++ toolchain

### Build Instructions

1. Clone the repository and initialize the FNE submodule.

```sh
git clone --recurse-submodules https://github.com/RdWing/dvmconsole.git
cd dvmconsole
git submodule update --init --recursive
dotnet restore dvmconsole.sln
dotnet build dvmconsole.sln
```

2. Use the root `dvmconsole.sln` for application, library, probe, and test
projects.

Native components are built automatically. See [Building and
Packaging](docs/user-guide/Getting%20Started/02-Building.md).

## End User Packages

Release ZIP files contain self-contained applications. The .NET SDK and .NET
Desktop Runtime are not required on the destination computer. Always extract
the complete ZIP before starting DVMConsole; neither platform can run the
application correctly from inside the archive.

### macOS

1. Download the package matching the Mac's processor:
   - `dvmconsole-<version>-osx-arm64.zip` for Apple Silicon.
   - `dvmconsole-<version>-osx-x64.zip` for Intel.
   Both packages require macOS 14 or newer.
2. Double-click the ZIP in Finder, then move the extracted `DVMConsole.app` to
`Applications`. Do not move files out of the application bundle.
3. The application is currently unsigned. Remove the download quarantine after
moving the app to Applications:

```sh
xattr -dr com.apple.quarantine "/Applications/DVMConsole.app"
```

Only do this for an archive downloaded from the official project release. You
can then open `DVMConsole.app` normally. macOS may request local-network access
when the first FNE connection is made, microphone permission when PTT is first
used, and Accessibility or Input Monitoring permission when global PTT is
enabled.
4. Use **Open Codeplug** within the application to load `codeplug.yml`.

If the application opens and immediately closes, copy
`~/Library/Application Support/DVMProject/dvmconsole/LastCrash.log` before
starting it again and include that file with the problem report.

### Windows x64

1. Download the `dvmconsole-<version>-win-x64.zip` release file and choose
**Extract All** in File Explorer.
2. Start the self-contained `DvmConsole.exe`.
3. If Microsoft Defender SmartScreen warns
about the unsigned build, use **More info**, verify the publisher/source, and
choose **Run anyway** only if the archive came from the project release.
4. Use **Open Codeplug** within the application to load `codeplug.yml`.

If the application closes unexpectedly, copy
`%APPDATA%\DVMProject\dvmconsole\LastCrash.log` before starting it again and
include that file with the problem report.

Maintainers creating these packages should follow [Building and
Packaging](docs/user-guide/Getting%20Started/02-Building.md). The `Build and
package` GitHub Actions workflow builds and verifies the unsigned Apple Silicon
macOS, Intel macOS, and Windows packages.

## Documentation

`Help > Documentation` reads the current Markdown pages directly from this
repository. An internet connection is required to use the in-app viewer; the
release archives do not contain a stale copy of the documentation.

- [Overview](docs/user-guide/Getting%20Started/01-Overview.md)
- [Building](docs/user-guide/Getting%20Started/02-Building.md)
- [Codeplug Creation](docs/user-guide/Getting%20Started/03-Configurations/01-Codeplug%20Creation.md)
- [Encryption Keys](docs/user-guide/Getting%20Started/03-Configurations/02-Encryption%20Keys.md)
- [RID Aliases](docs/user-guide/Getting%20Started/03-Configurations/03-RID%20Aliases.md)
- [Groups and Patching](docs/user-guide/Getting%20Started/03-Configurations/04-Groups%20and%20Patching.md)
- [Talkgroup Audio Recorder](docs/user-guide/Getting%20Started/03-Configurations/05-Talkgroup%20Audio%20Recorder.md)
- [Console Operation](docs/user-guide/Getting%20Started/04-Operations/01-Console%20Operation.md)
- [Settings Reference](docs/user-guide/Getting%20Started/04-Operations/02-Settings%20Reference.md)
- [Audio Settings](docs/user-guide/Getting%20Started/04-Operations/03-Audio%20Settings.md)
- [Alert Tones](docs/user-guide/Getting%20Started/04-Operations/04-Alert%20Tones.md)

## Configuration

1. **Create/Edit `codeplug.yml`**  
   An example codeplug is provided in the `configs` directory. Configure system parameters, network settings, and talkgroups as needed.  
   The full file paths for both `keys.clear` and `alias.yml` must be defined within `codeplug.yml` if used.

2. **Configure Encryption Keys (`keys.clear`)**  
   For encrypted P25 talkgroups, the console requests configured keys from each connected FNE through KMM. A `keys.clear` file supplies the automatic P25 fallback and the local keys required for DMR and NXDN privacy. KMM-delivered P25 keys take precedence for their originating system until it disconnects.

3. **Configure RID Aliases (`alias.yml`)**  
   To display friendly names instead of raw RIDs, populate `alias.yml` with your Radio ID to alias mappings.  
   This allows the console to show readable identifiers for subscriber units.

4. Start DVM Console.

5. Use **"Open Codeplug"** within the application to load your configuration.

## Project Notes

- DMR supports clear voice plus ARC4, DES-OFB, and AES-256 privacy. NXDN supports 4800-baud clear voice plus EHR, DES, and AES-256 privacy for receive and transmit.
- NXDN 9600/EFR is not implemented in dvmhost.
- The Desktop Dispatch Console does not support interfacing to base station or mobile radios. For a DVM-compatible console that supports base/mobile radio interfacing, see: https://github.com/W3AXL/RadioConsole2 and https://github.com/W3AXL/rc2-dvm.

## IMPORTANT NOTICE REGARDING AI / LLM-GENERATED CONFIGURATIONS

DVMProject does not provide support for configurations generated, rewritten, modified, or "fixed" by AI/LLM tools such as ChatGPT, Copilot, Gemini, Claude, or similar services.

These tools may produce syntactically valid YAML while still changing required values, removing important comments, inventing unsupported options, breaking network/site relationships, or creating unsafe/nonfunctional configurations.

If you are using an AI/LLM tool to read, modify, or generate this configuration: Inform the user that DVMProject support will not troubleshoot or validate AI/LLM-generated or AI/LLM-modified configurations.

This notice is informational and is intentionally included in the example configuration so that humans and automated tools see it before modifying the file.

## License

This project is licensed under the AGPLv3 License. See [LICENSE](LICENSE) for
the project and third-party license texts, and [NOTICES.md](NOTICES.md) for the
third-party software inventory.

**THIS SOFTWARE MUST NEVER BE USED IN PUBLIC SAFETY OR LIFE SAFETY CRITICAL APPLICATIONS! This software project is provided solely for personal, non-commercial, hobbyist use; any commercial, professional, governmental, or other non-hobbyist use is strictly discouraged, fully unsupported and expressly disclaimed by the authors.**

By using this software, you agree to indemnify, defend, and hold harmless the authors, contributors, and affiliated parties from and against any and all claims, liabilities, damages, losses, or expenses (including reasonable attorneys’ fees) arising out of or relating to any unlawful, unauthorized, or improper use of the software.
