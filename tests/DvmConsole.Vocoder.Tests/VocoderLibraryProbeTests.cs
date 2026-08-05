// SPDX-License-Identifier: AGPL-3.0-only
/**
* Managed tests for the dvmconsole/VocoderLibraryProbe.cs startup probe: a
* platform-neutral availability/symbol check for the native dvmvocoder
* `libvocoder` library (Linux .so / macOS .dylib / Windows .dll resolved by
* System.Runtime.InteropServices.NativeLibrary, never by a Windows DLL filename).
*
* Two run modes:
*
*   - With the verified native library discoverable, e.g.
*
*         LD_LIBRARY_PATH=/tmp/dvmvocoder_spike_60624/dvmvocoder/build \
*             dotnet test tests/DvmConsole.Vocoder.Tests/...
*
*     every probe test runs for real, including the native success cases.
*
*   - Without LD_LIBRARY_PATH (the no-native build check) the native-dependent
*     tests skip cleanly instead of failing with a non-actionable assertion,
*     and the no-native-safe tests still pass: the required-export contract,
*     the default-probe degradation path, the impossible-name missing-library
*     diagnostic, and (on Linux) nothing else. Native failures in this mode
*     surface only from the DllImport interop tests as DllNotFoundException.
*/

using System;
using System.IO;
using System.Runtime.InteropServices;
using dvmconsole;
using Xunit;

namespace DvmConsole.Vocoder.Tests
{
    public sealed class VocoderLibraryProbeTests
    {
        // The eight required DVMProject/dvmvocoder C ABI exports (encoder and
        // decoder create/encode/decode(./bits)/delete).
        private const string EncoderCreate = "MBEEncoder_Create";
        private const string EncoderEncode = "MBEEncoder_Encode";
        private const string EncoderEncodeBits = "MBEEncoder_EncodeBits";
        private const string EncoderDelete = "MBEEncoder_Delete";
        private const string DecoderCreate = "MBEDecoder_Create";
        private const string DecoderDecode = "MBEDecoder_Decode";
        private const string DecoderDecodeBits = "MBEDecoder_DecodeBits";
        private const string DecoderDelete = "MBEDecoder_Delete";

        private static readonly string[] AllEight =
        {
            EncoderCreate, EncoderEncode, EncoderEncodeBits, EncoderDelete,
            DecoderCreate, DecoderDecode, DecoderDecodeBits, DecoderDelete,
        };

        private const string BogusExport = "MBEEncoder_DefinitelyNotARealExport_13775f9c";
        private const string GuidanceUrl = "https://github.com/DVMProject/dvmvocoder";

        // ------------------------------------------------------------------
        // Required-export contract (no native library needed)
        // ------------------------------------------------------------------

        [Fact]
        public void RequiredExports_DeclaresTheEightContractSymbols()
        {
            Assert.Equal(8, VocoderLibraryProbe.RequiredExports.Length);
            Assert.Equal(AllEight, VocoderLibraryProbe.RequiredExports);
        }

        // ------------------------------------------------------------------
        // Default no-arg probe: the exact entry point the App startup uses.
        // No-native-safe: healthy (null) when libvocoder resolves, otherwise a
        // diagnostic through the same path as the named overload.
        // ------------------------------------------------------------------

        [Fact]
        public void Probe_NoArg_IsHealthyOrNamesLogicalLibraryAndGuidance()
        {
            string diagnostic = VocoderLibraryProbe.Probe();

            // When libvocoder resolves the startup probe reports healthy; when
            // it does not (no-native build) the default probe must degrade
            // through the same diagnostic path as the named overload: a
            // non-empty message naming the logical library and giving the
            // upstream guidance URL. A blank or empty message is never valid.
            if (diagnostic != null)
            {
                Assert.False(string.IsNullOrWhiteSpace(diagnostic),
                    "Probe() must return a diagnostic when libvocoder cannot be verified.");
                Assert.Contains(VocoderLibraryProbe.LogicalLibraryName, diagnostic);
                Assert.Contains(GuidanceUrl, diagnostic);
            }
        }

        // ------------------------------------------------------------------
        // Success against the verified native library
        // ------------------------------------------------------------------

        [Fact]
        public void Probe_ReturnsNull_WhenVerifiedNativeLibraryIsAvailable()
        {
            if (!IsLibvocoderResolvable())
                return; // no-native build: the missing-library tests carry this run
            Assert.Null(VocoderLibraryProbe.Probe());
        }

        [Fact]
        public void Probe_IsIdempotent_OnTheVerifiedNativeLibrary()
        {
            if (!IsLibvocoderResolvable())
                return; // no-native build: the missing-library tests carry this run
            Assert.Null(VocoderLibraryProbe.Probe());
            Assert.Null(VocoderLibraryProbe.Probe());
        }

        // ------------------------------------------------------------------
        // Missing library (no native library needed: impossible GUID name)
        // ------------------------------------------------------------------

        [Fact]
        public void Probe_MissingLibrary_ReturnsNonEmptyUsefulDiagnostic()
        {
            string missingName = "libvocoder_missing_probe_test_" + Guid.NewGuid().ToString("N");
            string diagnostic = VocoderLibraryProbe.Probe(missingName);

            Assert.False(string.IsNullOrWhiteSpace(diagnostic), "Probe must return a diagnostic when the library is missing.");
            Assert.Contains(missingName, diagnostic);
            Assert.Contains(GuidanceUrl, diagnostic);
        }

        // ------------------------------------------------------------------
        // Required-export validation (needs the real libvocoder to prove the
        // bogus symbol is genuinely absent, so it skips in a no-native build)
        // ------------------------------------------------------------------

        [Fact]
        public void Probe_MissingRequiredExport_ReturnsDiagnosticNamingTheSymbol()
        {
            if (!IsLibvocoderResolvable())
                return; // requires the verified native library

            var required = new[] { EncoderCreate, BogusExport };
            string diagnostic = VocoderLibraryProbe.Probe("libvocoder", required);

            Assert.NotNull(diagnostic);
            Assert.Contains(BogusExport, diagnostic);
            Assert.Contains(GuidanceUrl, diagnostic);
        }

        [Fact]
        public void Probe_MissingRequiredExport_NamesEveryMissingSymbol()
        {
            if (!IsLibvocoderResolvable())
                return; // requires the verified native library

            var required = new[] { DecoderDelete, BogusExport, "MBEEncoder_AnotherBogus_b1c2d3e4" };
            string diagnostic = VocoderLibraryProbe.Probe("libvocoder", required);

            Assert.NotNull(diagnostic);
            Assert.Contains(BogusExport, diagnostic);
            Assert.Contains("MBEEncoder_AnotherBogus_b1c2d3e4", diagnostic);
        }

        // ------------------------------------------------------------------
        // Handle freeing: a successfully loaded library must always be freed,
        // including when an export is missing. Verified on Linux by copying the
        // real .so to a unique temp path and checking /proc/self/maps after the
        // probe; if the probe leaked its dlopen handle the copy would stay mapped.
        // ------------------------------------------------------------------

        [Fact]
        public void Probe_FreesLibraryHandle_AfterSuccessfulLoad()
        {
            string copyPath = CopyRealLibraryToUniqueTempFile();
            if (copyPath == null)
                return; // not Linux / library not resolvable here; skip

            try
            {
                Assert.Null(VocoderLibraryProbe.Probe(copyPath));
                Assert.False(IsMapped(copyPath), "libvocoder copy must be unmapped after a successful probe.");
            }
            finally
            {
                File.Delete(copyPath);
            }
        }

        [Fact]
        public void Probe_FreesLibraryHandle_WhenExportMissing()
        {
            string copyPath = CopyRealLibraryToUniqueTempFile();
            if (copyPath == null)
                return; // not Linux / library not resolvable here; skip

            try
            {
                string diagnostic = VocoderLibraryProbe.Probe(copyPath, new[] { EncoderCreate, BogusExport });
                Assert.NotNull(diagnostic);
                Assert.Contains(BogusExport, diagnostic);
                Assert.False(IsMapped(copyPath), "libvocoder copy must be unmapped after a failed export probe.");
            }
            finally
            {
                File.Delete(copyPath);
            }
        }

        // ------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------

        private static bool IsLibvocoderResolvable()
        {
            if (NativeLibrary.TryLoad("libvocoder", out IntPtr handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }
            return false;
        }

        private static string CopyRealLibraryToUniqueTempFile()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return null; // probe-free verification uses /proc/self/maps (Linux only)

            string source = LocateResolvedLibraryPath();
            if (source == null)
                return null; // native library not resolvable in this run; skip

            string copyPath = Path.Combine(Path.GetTempPath(), "libvocoder_probe_" + Guid.NewGuid().ToString("N") + ".so");
            File.Copy(source, copyPath);
            return copyPath;
        }

        private static string LocateResolvedLibraryPath()
        {
            if (!NativeLibrary.TryLoad("libvocoder", out IntPtr handle))
                return null;
            try
            {
                foreach (string line in File.ReadAllLines("/proc/self/maps"))
                {
                    int sep = line.LastIndexOf(' ');
                    if (sep >= 0 && line.Substring(sep + 1).Contains("libvocoder.so"))
                        return line.Substring(sep + 1);
                }
            }
            finally
            {
                NativeLibrary.Free(handle);
            }
            return null;
        }

        private static bool IsMapped(string path)
        {
            foreach (string line in File.ReadAllLines("/proc/self/maps"))
            {
                if (line.EndsWith(path, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }
}
