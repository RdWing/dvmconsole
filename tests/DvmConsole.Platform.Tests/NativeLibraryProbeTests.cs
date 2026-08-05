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
