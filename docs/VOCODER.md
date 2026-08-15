# Software vocoder

The rebuild uses the software vocoder through `DvmConsole.Vocoder.IVocoderBackend`.
The default implementation is `SoftwareVocoderBackend`, which loads the native
`dvmvocoder` shared library at runtime:

- macOS: `libvocoder.dylib`
- Windows: `libvocoder.dll`
- Linux: `libvocoder.so`

The native library is intentionally not checked into this repository. Build it
from [DVMProject/dvmvocoder](https://github.com/DVMProject/dvmvocoder):

```sh
git clone https://github.com/DVMProject/dvmvocoder.git
cmake -S dvmvocoder -B dvmvocoder-build -DCMAKE_BUILD_TYPE=Release
cmake --build dvmvocoder-build --config Release
```

On macOS this requires Xcode Command Line Tools and produces a dynamic library
named `libvocoder.dylib`. On Windows, run the same commands from a Developer
PowerShell with the Visual Studio C++ workload installed; multi-configuration
generators normally place `libvocoder.dll` under the build directory's
`Release` folder. Use the actual generated path for `DVMVOCODER_LIBRARY`.

For local application development, point the runtime at that library:

```sh
# macOS
export DVMVOCODER_LIBRARY=/full/path/to/libvocoder.dylib
```

```powershell
# Windows
$env:DVMVOCODER_LIBRARY = "C:\full\path\to\libvocoder.dll"
```

Run the native verification tests by providing the built library explicitly:

```sh
DVMVOCODER_LIBRARY=/path/to/libvocoder.dylib \
  dotnet test src/DvmConsole.Vocoder.Tests/DvmConsole.Vocoder.Tests.csproj \
  --no-restore /p:UseSharedCompilation=false
```

`VocoderMode` preserves the legacy DMR AMBE and P25 IMBE modes. A future
`AMBE.DLL` implementation should be added as another `IVocoderBackend`; the
application core should not call vendor-specific native entry points directly.

NXDN is kept separate from those two modes. Its 48-byte NXDD payload is passed
only to an explicitly injected `INxdnVocoderBackend` through the receive
coordinator. The default desktop construction does not provide one and fails
closed until a real FEC/AMBE+2 implementation is supplied; an unavailable
backend never causes audio infrastructure to open.
