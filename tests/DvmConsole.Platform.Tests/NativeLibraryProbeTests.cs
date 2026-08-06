// SPDX-License-Identifier: AGPL-3.0-only
/**
* Dedicated contract gate for the DvmConsole.Platform.Native.NativeLibraryProbe
* adapter (the INativeLibraryProbe implementation). These facts are written
* entirely against the agreed contract: the probe takes logical library names
* (never OS file names), requires at least one export symbol, returns
* NativeLibraryProbeResult values instead of throwing for probeable-but-missing
* libraries, and is stateless across DisposeAsync.
*/
#nullable enable
using System.Runtime.InteropServices;
using DvmConsole.Platform.Native;
using Xunit;

namespace DvmConsole.Platform.Tests
{
    /// <summary>
    /// Contract gate for <c>NativeLibraryProbe</c> against the
    /// <see cref="INativeLibraryProbe"/> interface.
    /// </summary>
    public sealed class NativeLibraryProbeTests
    {
        private static NativeLibraryProbe CreateProbe() => new();

        /// <summary>
        /// A null logical name is a programming error, not a probe outcome.
        /// </summary>
        [Fact]
        public void NullLogicalName_ThrowsArgumentException()
        {
            var probe = CreateProbe();

            Assert.Throws<ArgumentException>(
                () => probe.Probe(null!, new[] { "malloc" }));
        }

        /// <summary>
        /// A whitespace-only logical name is a programming error, not a probe
        /// outcome.
        /// </summary>
        [Fact]
        public void WhitespaceLogicalName_ThrowsArgumentException()
        {
            var probe = CreateProbe();

            Assert.Throws<ArgumentException>(
                () => probe.Probe(" \t ", new[] { "malloc" }));
        }

        /// <summary>
        /// A null export list is a programming error, not a probe outcome.
        /// </summary>
        [Fact]
        public void NullRequiredExports_ThrowsArgumentNullException()
        {
            var probe = CreateProbe();

            Assert.Throws<ArgumentNullException>(
                () => probe.Probe("dvmvocoder", null!));
        }

        /// <summary>
        /// Probing without declaring at least one required export is a
        /// programming error, not a probe outcome.
        /// </summary>
        [Fact]
        public void EmptyRequiredExports_ThrowsArgumentException()
        {
            var probe = CreateProbe();

            Assert.Throws<ArgumentException>(
                () => probe.Probe("dvmvocoder", Array.Empty<string>()));
        }

        /// <summary>
        /// The probe takes logical library names, never OS file names with a
        /// platform extension.
        /// </summary>
        [Fact]
        public void FileStyleLogicalNames_ThrowArgumentException()
        {
            var probe = CreateProbe();

            foreach (var logicalName in new[] { "dvmvocoder.dll", "dvmvocoder.so", "dvmvocoder.dylib" })
            {
                Assert.Throws<ArgumentException>(
                    () => probe.Probe(logicalName, new[] { "malloc" }));
            }
        }

        /// <summary>
        /// The probe takes logical library names, never OS file names or
        /// paths: path separators and framework bundle names are rejected
        /// exactly like platform extensions.
        /// </summary>
        [Fact]
        public void PathAndFrameworkStyleLogicalNames_ThrowArgumentException()
        {
            var probe = CreateProbe();

            foreach (var logicalName in new[]
            {
                "/usr/lib/libSystem.B.dylib",
                "CoreAudio.framework/CoreAudio",
                "CoreAudio.framework",
                "AudioToolbox.FRAMEWORK",
                "bin\\vocoder.dll",
                "C:\\Windows\\System32\\kernel32.dll",
            })
            {
                Assert.Throws<ArgumentException>(
                    () => probe.Probe(logicalName, new[] { "malloc" }));
            }
        }

        /*
        ** macOS install-path mapping (host-independent)
        */

        /// <summary>
        /// On macOS the three P/Invoke framework libraries are resolved to
        /// their install paths so the OS loader can find them.
        /// </summary>
        [Theory]
        [InlineData("CoreAudio", "/System/Library/Frameworks/CoreAudio.framework/CoreAudio")]
        [InlineData("AudioToolbox", "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox")]
        [InlineData("libSystem", "/usr/lib/libSystem.B.dylib")]
        public void ResolveLoadName_OnMacOs_MapsTheThreePInvokeLibrariesToInstallPaths(
            string logicalName, string expectedLoadName)
        {
            Assert.Equal(expectedLoadName, NativeLibraryProbe.ResolveLoadName(logicalName, isMacOS: true));
        }

        /// <summary>
        /// The macOS mapping matches the three logical names
        /// case-insensitively, so framework names in any casing still reach
        /// the right install path.
        /// </summary>
        [Theory]
        [InlineData("coreaudio", "/System/Library/Frameworks/CoreAudio.framework/CoreAudio")]
        [InlineData("COREAUDIO", "/System/Library/Frameworks/CoreAudio.framework/CoreAudio")]
        [InlineData("audiotoolbox", "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox")]
        [InlineData("AudioTOOLBOX", "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox")]
        [InlineData("libsystem", "/usr/lib/libSystem.B.dylib")]
        [InlineData("LIBSYSTEM", "/usr/lib/libSystem.B.dylib")]
        public void ResolveLoadName_OnMacOs_MatchesTheThreeLogicalNamesCaseInsensitively(
            string logicalName, string expectedLoadName)
        {
            Assert.Equal(expectedLoadName, NativeLibraryProbe.ResolveLoadName(logicalName, isMacOS: true));
        }

        /// <summary>
        /// The macOS mapping is exact: names that merely contain a mapped
        /// name, and unknown names, fall through unchanged.
        /// </summary>
        [Fact]
        public void ResolveLoadName_OnMacOs_FallsThroughUnchangedForOtherNames()
        {
            Assert.Equal("dvmvocoder", NativeLibraryProbe.ResolveLoadName("dvmvocoder", isMacOS: true));
            Assert.Equal("libc", NativeLibraryProbe.ResolveLoadName("libc", isMacOS: true));
            Assert.Equal("CoreAudioKit", NativeLibraryProbe.ResolveLoadName("CoreAudioKit", isMacOS: true));
            Assert.Equal("libSystemExtra", NativeLibraryProbe.ResolveLoadName("libSystemExtra", isMacOS: true));
        }

        /// <summary>
        /// Off macOS every logical name passes through unchanged, including
        /// the three framework names: the mapping is the macOS exception, not
        /// a general rewrite.
        /// </summary>
        [Fact]
        public void ResolveLoadName_OffMacOs_FallsThroughUnchangedForEveryName()
        {
            Assert.Equal("CoreAudio", NativeLibraryProbe.ResolveLoadName("CoreAudio", isMacOS: false));
            Assert.Equal("AudioToolbox", NativeLibraryProbe.ResolveLoadName("AudioToolbox", isMacOS: false));
            Assert.Equal("libSystem", NativeLibraryProbe.ResolveLoadName("libSystem", isMacOS: false));
            Assert.Equal("dvmvocoder", NativeLibraryProbe.ResolveLoadName("dvmvocoder", isMacOS: false));
        }

        /// <summary>
        /// The macOS install-path mapping never leaks into public results: a
        /// probe that applies the mapping and fails still reports the
        /// caller's exact logical name (original casing included) in the
        /// result and the diagnostic — never the mapped path.
        /// </summary>
        [Fact]
        public void Probe_WithMacOsMapping_UnloadableFrameworkName_FailurePreservesOriginalLogicalName()
        {
            // Forced macOS mapping on any host: "coreaudio" maps to the
            // CoreAudio framework install path. On non-macOS hosts the load
            // fails; on macOS the required bogus export is missing. Either
            // way the failure must carry the caller's logical name, not the
            // mapped path.
            var probe = new NativeLibraryProbe(isMacOS: true);

            var result = probe.Probe("coreaudio", new[] { "dvmconsole_export_that_does_not_exist" });

            Assert.False(result.IsSuccess);
            Assert.Equal("coreaudio", result.LogicalName);
            Assert.NotNull(result.Diagnostic);
            Assert.Contains("coreaudio", result.Diagnostic);
            Assert.DoesNotContain("/System/Library/Frameworks/CoreAudio.framework", result.Diagnostic);
        }

        /// <summary>
        /// A well-formed logical name that resolves to nothing is a failure
        /// result that preserves the logical name and names it in the
        /// diagnostic.
        /// </summary>
        [Fact]
        public void MissingGuidSuffixedLibrary_ReturnsFailureNamingTheLibrary()
        {
            const string logicalName = "dvmvocoder-4f8b2c1a-9d3e-4f6a-8b7c-2e5a1d4f9c3b";
            var probe = CreateProbe();

            var result = probe.Probe(logicalName, new[] { "MBEEncoder_Create" });

            Assert.False(result.IsSuccess);
            Assert.Equal(logicalName, result.LogicalName);
            Assert.NotNull(result.Diagnostic);
            Assert.Contains(logicalName, result.Diagnostic);
        }

        /// <summary>
        /// The probe must resolve a real system library by logical name on
        /// every supported OS and verify its real exports. Unknown platforms
        /// fail loudly rather than skip.
        /// </summary>
        [Fact]
        public void SystemLibrary_ProbeSucceeds()
        {
            var (logicalName, exports) = SystemLibraryFixture();
            var probe = CreateProbe();

            var result = probe.Probe(logicalName, exports);

            Assert.True(result.IsSuccess);
            Assert.Equal(logicalName, result.LogicalName);
            Assert.Null(result.Diagnostic);
        }

        /// <summary>
        /// A resolvable library whose export check fails must report every
        /// missing export by name in the diagnostic.
        /// </summary>
        [Fact]
        public void SystemLibrary_WithBogusExports_ReportsBothMissing()
        {
            const string bogusOne = "dvmconsole_export_that_does_not_exist_one";
            const string bogusTwo = "dvmconsole_export_that_does_not_exist_two";
            var (logicalName, exports) = SystemLibraryFixture();
            var probe = CreateProbe();

            var result = probe.Probe(
                logicalName,
                exports.Concat(new[] { bogusOne, bogusTwo }).ToArray());

            Assert.False(result.IsSuccess);
            Assert.NotNull(result.Diagnostic);
            Assert.Contains(bogusOne, result.Diagnostic);
            Assert.Contains(bogusTwo, result.Diagnostic);
        }

        /// <summary>
        /// DisposeAsync is a no-op for probe state: disposing twice must not
        /// stop the probe from working afterwards.
        /// </summary>
        [Fact]
        public async Task DisposeTwice_ThenProbe_StillSucceeds()
        {
            var (logicalName, exports) = SystemLibraryFixture();
            var probe = CreateProbe();

            await probe.DisposeAsync();
            await probe.DisposeAsync();

            var result = probe.Probe(logicalName, exports);

            Assert.True(result.IsSuccess);
            Assert.Equal(logicalName, result.LogicalName);
            Assert.Null(result.Diagnostic);
        }

        /// <summary>
        /// The real system library every supported OS is guaranteed to expose,
        /// with exports known to exist on that OS.
        /// </summary>
        private static (string LogicalName, string[] Exports) SystemLibraryFixture()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return ("libc", new[] { "malloc", "free" });
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return ("libSystem", new[] { "malloc", "free" });
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return ("kernel32", new[] { "GetCurrentProcessId", "Sleep" });
            }

            throw new PlatformNotSupportedException(
                $"No system library probe fixture for OS platform {RuntimeInformation.OSDescription}.");
        }
    }
}
