// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
/**
* RED contract gate for the About-dialog slice (plan Task 12 follow-on,
* WPF parity dvmconsole/AboutWindow.xaml.cs):
*
*   DvmConsole.Avalonia.ViewModels.AboutWindowViewModel
*
* The view model renders the About content: product name, version
* information (RxxAyy release + short commit hash), and the AGPL
* license notice. The version-string parsing is PURE and headless —
* the exact WPF three-case hash extraction (AboutWindow.xaml.cs):
*   Case 1: "R01A02 (abcdef12...)"  -> hash from inside parens, 7 chars
*   Case 2: "R01A02+2919e2e..."     -> hash from +buildmetadata, 7 chars
*   Case 3: space-separated fallback -> first token, 7 chars
* Release: R{major:D2}A{minor:D2} from the assembly version; Unknown
* when no version is supplied. Malformed input degrades to "unknown"
* hash and never throws.
*/
using System;
using DvmConsole.Avalonia.ViewModels;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract gate for <see cref="AboutWindowViewModel"/>.
    /// </summary>
    public sealed class AboutWindowViewModelTests
    {
        /* ------------------------------------------------------------------
        ** Surface
        ** ---------------------------------------------------------------- */

        [Fact]
        public void ApiShape_ExactPublicSurface()
        {
            var type = typeof(AboutWindowViewModel);
            Assert.True(type.IsSealed);
            Assert.NotNull(type.GetConstructor(new[]
            {
                typeof(string), typeof(string), typeof(string),
            }));
            Assert.NotNull(type.GetProperty("ProductName"));
            Assert.NotNull(type.GetProperty("ProductSubtitle"));
            Assert.NotNull(type.GetProperty("VersionLine"));
            Assert.NotNull(type.GetProperty("LicenseLine"));
            Assert.NotNull(type.GetProperty("LicenseUrl"));
            Assert.NotNull(type.GetProperty("RepositoryUrl"));
        }

        /* ------------------------------------------------------------------
        ** Version parsing
        ** ---------------------------------------------------------------- */

        [Fact]
        public void Version_ReleaseRxxAyy_FromAssemblyVersion()
        {
            var vm = new AboutWindowViewModel(
                "Digital Voice Modem", "Desktop Dispatch Console",
                Version.Parse("1.2.3.4"));

            Assert.Equal("R01A02", vm.ReleaseVersion);
        }

        [Fact]
        public void Version_NoAssemblyVersion_Unknown()
        {
            var vm = new AboutWindowViewModel(
                "Digital Voice Modem", "Desktop Dispatch Console", null);

            Assert.Equal("Unknown", vm.ReleaseVersion);
        }

        [Fact]
        public void Hash_Case1_Parenthesized()
        {
            var vm = new AboutWindowViewModel(
                "D", "S", Version.Parse("1.0.0.0"), "R01A00 (abcdef123456789)");

            Assert.Equal("abcdef1", vm.ShortHash);
        }

        [Fact]
        public void Hash_Case2_PlusBuildMetadata()
        {
            var vm = new AboutWindowViewModel(
                "D", "S", Version.Parse("1.0.0.0"), "R01A00+2919e2e1234");

            Assert.Equal("2919e2e", vm.ShortHash);
        }

        [Fact]
        public void Hash_Case3_SpaceSeparatedFallback()
        {
            var vm = new AboutWindowViewModel(
                "D", "S", Version.Parse("1.0.0.0"), "R01A00 1234567 extra");

            Assert.Equal("1234567", vm.ShortHash);
        }

        [Fact]
        public void Hash_NoInformationalVersion_Unknown()
        {
            var vm = new AboutWindowViewModel(
                "D", "S", Version.Parse("1.0.0.0"), null);

            Assert.Equal("unknown", vm.ShortHash);
        }

        [Fact]
        public void Hash_Malformed_Unknown_NeverThrows()
        {
            foreach (var info in new[] { "", "   ", "()", "+", "R01A00 (" })
            {
                var vm = new AboutWindowViewModel("D", "S", Version.Parse("1.0.0.0"), info);
                Assert.Equal("unknown", vm.ShortHash);
            }
        }

        [Fact]
        public void Hash_ShortHash_CappedAtSeven()
        {
            var vm = new AboutWindowViewModel(
                "D", "S", Version.Parse("1.0.0.0"), "R01A00 (abcd)");

            Assert.Equal("abcd", vm.ShortHash); // shorter than 7: kept whole
        }

        /* ------------------------------------------------------------------
        ** Content
        ** ---------------------------------------------------------------- */

        [Fact]
        public void Content_ProductNameAndSubtitle()
        {
            var vm = new AboutWindowViewModel(
                "Digital Voice Modem", "Desktop Dispatch Console", null);

            Assert.Equal("Digital Voice Modem", vm.ProductName);
            Assert.Equal("Desktop Dispatch Console", vm.ProductSubtitle);
        }

        [Fact]
        public void Content_LicenseAndLinks()
        {
            var vm = new AboutWindowViewModel(
                "Digital Voice Modem", "Desktop Dispatch Console", null);

            Assert.Contains("AGPL", vm.LicenseLine);
            Assert.Equal("https://opensource.org/licenses/AGPL-3.0", vm.LicenseUrl);
            Assert.Equal("https://github.com/RdWing/dvmconsole", vm.RepositoryUrl);
        }

        [Fact]
        public void Content_VersionLine_CombinesReleaseAndHash()
        {
            var vm = new AboutWindowViewModel(
                "D", "S", Version.Parse("1.2.0.0"), "R01A02 (abcdef123456789)");

            Assert.Equal("R01A02 (abcdef1)", vm.VersionLine);
        }
    }
}
