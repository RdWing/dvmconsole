// SPDX-License-Identifier: AGPL-3.0-only
/**
* Deterministic compile-smoke contract tests for the portable
* dvmconsole.ResourceIdentity (DvmConsole.Core). These lock the stable
* per-resource key contract (system + talkgroup/slot identity) that
* SettingsManager, TarManager and the WPF windows consume, mirroring the
* behavior of the current WPF implementation so the portable type can be
* verified without a UI.
*/
using System.Globalization;
using System.Reflection;
using dvmconsole;
using Xunit;

namespace DvmConsole.Core.Tests
{
    /// <summary>
    /// Compile-smoke contract tests for <see cref="ResourceIdentity"/>.
    /// </summary>
    public class ResourceIdentityTests
    {
        /// <summary>
        /// Null system and TGID compose the empty identity with the exact
        /// separator.
        /// </summary>
        [Fact]
        public void Build_NullNull_ReturnsSeparatorOnly()
        {
            Assert.Equal("|", ResourceIdentity.Build(null, null));
        }

        /// <summary>
        /// Null/blank systems and TGIDs normalize to empty segments, so every
        /// degenerate input still composes the exact "|" shape.
        /// </summary>
        [Fact]
        public void Build_NullAndBlankInputs_NormalizeToEmptySegments()
        {
            Assert.Equal("|1", ResourceIdentity.Build(null, "1"));
            Assert.Equal("|", ResourceIdentity.Build("", ""));
            Assert.Equal("|", ResourceIdentity.Build("   ", "   "));
            Assert.Equal("dmr|", ResourceIdentity.Build("DMR", null));
        }

        /// <summary>
        /// Both segments are trimmed; the system is lowercased invariantly;
        /// the TGID keeps its case after trimming.
        /// </summary>
        [Fact]
        public void Build_TrimsAndLowercasesSystemOnly()
        {
            Assert.Equal("dmr|123", ResourceIdentity.Build("  DMR  ", "  123  "));
            Assert.Equal("dmr|AbC", ResourceIdentity.Build("  dmr  ", "  AbC  "));
        }

        /// <summary>
        /// Internal whitespace is preserved: trimming only removes leading
        /// and trailing whitespace, never the inside of a segment.
        /// </summary>
        [Fact]
        public void Build_PreservesInternalWhitespace()
        {
            Assert.Equal("d m r|1 2", ResourceIdentity.Build("D M R", "1 2"));
        }

        /// <summary>
        /// The slot suffix is carried verbatim after the exact "|" separator
        /// and is not lowercased.
        /// </summary>
        [Fact]
        public void Build_ComposesExactSeparatorAndSlotSuffix()
        {
            Assert.Equal("dmr|1", ResourceIdentity.Build("DMR", "1"));
            Assert.Equal("dmr|1.2", ResourceIdentity.Build("DMR", "1.2"));
            Assert.Equal("dmr|1.2B", ResourceIdentity.Build("DMR", "1.2B"));
        }

        /// <summary>
        /// Lowercasing is culture invariant: under the Turkish tr-TR culture,
        /// "I" must fold to "i" (never the Turkish dotless "ı") and Unicode
        /// umlauts fold to their lowercase forms.
        /// </summary>
        [Fact]
        public void Build_IsCultureInvariantUnderTrTR()
        {
            CultureInfo originalCulture = CultureInfo.CurrentCulture;
            CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
                CultureInfo.CurrentUICulture = new CultureInfo("tr-TR");

                Assert.Equal("i|1", ResourceIdentity.Build("I", "1"));
                Assert.Equal("äöü|1", ResourceIdentity.Build("ÄÖÜ", "1"));
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
                CultureInfo.CurrentUICulture = originalUiCulture;
            }
        }

        /// <summary>
        /// Null, empty and whitespace-only names are all equivalent to one
        /// another and to themselves.
        /// </summary>
        [Fact]
        public void SystemMatches_NullEmptyWhitespace_AreEquivalent()
        {
            Assert.True(ResourceIdentity.SystemMatches(null, null));
            Assert.True(ResourceIdentity.SystemMatches(null, string.Empty));
            Assert.True(ResourceIdentity.SystemMatches(string.Empty, "   "));
            Assert.True(ResourceIdentity.SystemMatches("   ", null));
        }

        /// <summary>
        /// Names are compared trimmed and case-insensitively by ordinal
        /// rules, so surrounding whitespace and case never matter.
        /// </summary>
        [Fact]
        public void SystemMatches_TrimsAndIgnoresCase()
        {
            Assert.True(ResourceIdentity.SystemMatches("DMR", "dmr"));
            Assert.True(ResourceIdentity.SystemMatches("  DMR  ", "dmr"));
            Assert.True(ResourceIdentity.SystemMatches("DMR", "DMR "));
            Assert.True(ResourceIdentity.SystemMatches("ÄÖÜ", "äöü"));
        }

        /// <summary>
        /// Distinct names (beyond case and whitespace) do not match. A zero-width
        /// space is intentionally not treated as trim whitespace by .NET.
        /// </summary>
        [Fact]
        public void SystemMatches_DistinctNames_DoNotMatch()
        {
            Assert.False(ResourceIdentity.SystemMatches("DMR", "DMR2"));
            Assert.False(ResourceIdentity.SystemMatches("dmr", "DMR " + "x"));
            Assert.False(ResourceIdentity.SystemMatches("dmr", "DMR\u200B"));
        }

        /// <summary>
        /// Ordinal comparison is unaffected by the Turkish tr-TR culture: "I"
        /// and "i" must match even when the ambient culture would treat them
        /// differently.
        /// </summary>
        [Fact]
        public void SystemMatches_IsCultureInvariantUnderTrTR()
        {
            CultureInfo originalCulture = CultureInfo.CurrentCulture;
            CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
                CultureInfo.CurrentUICulture = new CultureInfo("tr-TR");

                Assert.True(ResourceIdentity.SystemMatches("I", "i"));
                Assert.False(ResourceIdentity.SystemMatches("I", "ı"));
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
                CultureInfo.CurrentUICulture = originalUiCulture;
            }
        }

        /// <summary>
        /// The contract lives in the portable DvmConsole.Core assembly, not
        /// in the WPF app or fnecore.
        /// </summary>
        [Fact]
        public void Type_AssemblyIsDvmConsoleCore()
        {
            Assert.Equal("DvmConsole.Core", typeof(ResourceIdentity).Assembly.GetName().Name);
        }

        /// <summary>
        /// The type is a static class exposing exactly the two public static
        /// methods of the contract: Build(string, string) -> string and
        /// SystemMatches(string, string) -> bool.
        /// </summary>
        [Fact]
        public void Type_IsStaticClassWithExactContractShape()
        {
            Assert.True(typeof(ResourceIdentity).IsClass);
            Assert.True(typeof(ResourceIdentity).IsAbstract);
            Assert.True(typeof(ResourceIdentity).IsSealed);

            MethodInfo build = typeof(ResourceIdentity).GetMethod("Build", new[] { typeof(string), typeof(string) });
            Assert.NotNull(build);
            Assert.True(build.IsPublic);
            Assert.True(build.IsStatic);
            Assert.Equal(typeof(string), build.ReturnType);

            MethodInfo systemMatches = typeof(ResourceIdentity).GetMethod("SystemMatches", new[] { typeof(string), typeof(string) });
            Assert.NotNull(systemMatches);
            Assert.True(systemMatches.IsPublic);
            Assert.True(systemMatches.IsStatic);
            Assert.Equal(typeof(bool), systemMatches.ReturnType);

            string[] declared = typeof(ResourceIdentity)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.DeclaringType == typeof(ResourceIdentity))
                .Select(m => m.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(new[] { "Build", "SystemMatches" }, declared);
        }
    }
}
