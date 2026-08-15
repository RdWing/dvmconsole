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
