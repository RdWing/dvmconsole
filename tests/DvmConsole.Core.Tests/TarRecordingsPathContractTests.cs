// SPDX-License-Identifier: AGPL-3.0-only
/**
* RED contract gate for extracting the duplicated TAR recordings-root
* normalization ternary into a pure Core helper:
*
*     string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim()
*
* Today that ternary is copy-pasted at six call sites in the WPF app
* (dvmconsole/SettingsManager.cs LoadSettings ~L637, NormalizeSettings ~L947,
* SaveTarSettings ~L1691; dvmconsole/TarManager.cs GetConfiguredRecordingRoot
* ~L72 and TryEnsureRecordingRoot ~L380; dvmconsole/TarConfigurationWindow.xaml.cs
* constructor ~L127). This file locks the agreed
* extraction surface before any production code exists:
*
*   namespace dvmconsole
*   public static class TarRecordingsPath
*   {
*       public static string Resolve(string configuredPath, string fallbackPath);
*   }
*
* Contract, exactly as the six call sites behave today: null, empty or
* whitespace-only configuredPath returns fallbackPath unchanged (same string
* instance, relative or absolute); any non-empty configuredPath is returned
* trimmed of surrounding whitespace but otherwise preserved verbatim, including
* relative paths and interior whitespace. The helper enforces no rootedness
* and performs no filesystem I/O. All assertions are deterministic and hermetic.
*/
using System.Reflection;
using dvmconsole;
using Xunit;

namespace DvmConsole.Core.Tests
{
    /// <summary>
    /// RED contract tests for <see cref="TarRecordingsPath"/> — written before
    /// the production type exists, so the build failure below is the genuine
    /// missing-type gate for the extraction step.
    /// </summary>
    public class TarRecordingsPathContractTests
    {
        private static readonly string HermeticAbsolutePath =
            Path.Combine(Path.GetTempPath(), "dvmconsole-tar-recordings-contract");

        /// <summary>
        /// A null configured path falls back to the fallback path, returned
        /// unchanged (the same string instance).
        /// </summary>
        [Fact]
        public void NullConfigured_ReturnsFallbackVerbatim()
        {
            string fallback = HermeticAbsolutePath;

            string result = TarRecordingsPath.Resolve(null, fallback);

            Assert.Same(fallback, result);
        }

        /// <summary>
        /// string.Empty is the legacy "no configured root" value: it must
        /// behave exactly like null.
        /// </summary>
        [Fact]
        public void EmptyConfigured_ReturnsFallbackVerbatim()
        {
            string fallback = HermeticAbsolutePath;

            string result = TarRecordingsPath.Resolve(string.Empty, fallback);

            Assert.Same(fallback, result);
        }

        /// <summary>
        /// Whitespace-only input is not a real path: the fallback is returned
        /// unchanged, exactly as IsNullOrWhiteSpace requires.
        /// </summary>
        [Fact]
        public void WhitespaceConfigured_ReturnsFallbackVerbatim()
        {
            string fallback = HermeticAbsolutePath;

            string result = TarRecordingsPath.Resolve("   \t\r\n  ", fallback);

            Assert.Same(fallback, result);
        }

        /// <summary>
        /// A non-empty absolute path with surrounding whitespace is trimmed;
        /// the resulting value is the exact absolute path with no other
        /// changes.
        /// </summary>
        [Fact]
        public void WhitespacePaddedAbsolutePath_IsTrimmedToAbsolutePath()
        {
            string result = TarRecordingsPath.Resolve(
                "  " + HermeticAbsolutePath + "  ",
                "/unused/fallback");

            Assert.Equal(HermeticAbsolutePath, result);
            Assert.True(Path.IsPathRooted(result));
        }

        /// <summary>
        /// Relative configured paths are preserved after trimming: no
        /// rootedness enforcement, and interior whitespace survives untouched.
        /// </summary>
        [Fact]
        public void RelativeConfiguredPath_IsTrimmedAndPreservedRelative()
        {
            string result = TarRecordingsPath.Resolve(
                "  recordings/custom dir/sub folder  ",
                "/unused/fallback");

            Assert.Equal("recordings/custom dir/sub folder", result);
            Assert.False(Path.IsPathRooted(result));
        }

        /// <summary>
        /// The fallback itself is never normalized: a relative fallback is
        /// returned unchanged when the configured path is missing.
        /// </summary>
        [Fact]
        public void RelativeFallback_IsReturnedUnchanged()
        {
            string fallback = "recordings/fallback dir";

            string result = TarRecordingsPath.Resolve(null, fallback);

            Assert.Same(fallback, result);
            Assert.False(Path.IsPathRooted(result));
        }

        /// <summary>
        /// The helper is a portable DvmConsole.Core type — the same assembly
        /// the rest of the Core path/configuration contract lives in — so the
        /// WPF app can consume it without a Core dependency on the app.
        /// </summary>
        [Fact]
        public void TarRecordingsPath_Type_AssemblyIsDvmConsoleCore()
        {
            Assert.Equal("DvmConsole.Core", typeof(TarRecordingsPath).Assembly.GetName().Name);
        }

        /// <summary>
        /// The helper surface is exactly a static class with one public static
        /// method — Resolve(string, string) returning string. No public fields,
        /// properties, constructors or instance methods: nothing for the helper
        /// to write state (or files) through.
        /// </summary>
        [Fact]
        public void TarRecordingsPath_Surface_IsPureStaticSingleMethodHelper()
        {
            // Static classes are abstract and sealed; they expose no public
            // constructors and no instance members.
            Assert.True(typeof(TarRecordingsPath).IsAbstract);
            Assert.True(typeof(TarRecordingsPath).IsSealed);
            Assert.Empty(typeof(TarRecordingsPath).GetConstructors());
            Assert.Empty(typeof(TarRecordingsPath).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
            Assert.Empty(typeof(TarRecordingsPath).GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance));
            Assert.Empty(typeof(TarRecordingsPath).GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance));

            // Exactly one public static method: Resolve(string, string) -> string.
            var methods = typeof(TarRecordingsPath).GetMethods(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
            var resolve = Assert.Single(methods);

            Assert.Equal("Resolve", resolve.Name);
            Assert.True(resolve.IsStatic);
            Assert.Equal(typeof(string), resolve.ReturnType);
            Assert.Equal(
                new[] { typeof(string), typeof(string) },
                resolve.GetParameters().Select(p => p.ParameterType));
        }
    }
}
