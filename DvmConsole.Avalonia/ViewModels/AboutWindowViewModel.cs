// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Runtime.InteropServices;

namespace DvmConsole.Avalonia.ViewModels
{
    /// <summary>
    /// Renders the About-dialog content: product identity, the RxxAyy
    /// release line with the short commit hash, and the AGPL license
    /// notice. The version-string parsing is pure and headless — WPF
    /// parity with dvmconsole/AboutWindow.xaml.cs (the three-case
    /// informational-version hash extraction).
    ///
    /// Deliberate deviations from the WPF oracle (all RED-pinned as
    /// improvements): the hash cap is 7 chars (WPF keeps exactly-8-char
    /// hashes whole — an off-by-one); the space-separated fallback takes
    /// the FIRST token (WPF takes the last, which emits garbage for
    /// 3+ tokens); malformed input (lone/unclosed parens, bare '+')
    /// degrades to "unknown" instead of falling through to garbage.
    /// The VersionLine is "RxxAyy (hash)" without WPF's "\nBuilt: ..."
    /// suffix (avoids file I/O in the code-behind).
    /// </summary>
    public sealed class AboutWindowViewModel
    {
        /// <summary>
        /// The AGPL license notice sentence (WPF AboutWindow.xaml parity).
        /// </summary>
        private const string LicenseNotice =
            "This software is licensed under the GNU Affero General Public License v3 (AGPLv3).";

        /// <summary>
        /// The license URL (WPF AboutWindow.xaml parity).
        /// </summary>
        private const string LicenseLink = "https://opensource.org/licenses/AGPL-3.0";

        /// <summary>
        /// The source repository URL.
        /// </summary>
        private const string RepositoryLink = "https://github.com/RdWing/dvmconsole";

        /// <summary>
        /// The documentation tree published with the upstream release line.
        /// </summary>
        public const string DocumentationLink =
            "https://github.com/DVMProject/dvmconsole/tree/r01a02_dev/dvmconsole/Docs";

        /// <summary>
        /// Creates the view model from the assembly version and the
        /// informational version (e.g. "R01A02+2919e2e...").
        /// </summary>
        public AboutWindowViewModel(
            string productName,
            string productSubtitle,
            Version? assemblyVersion,
            string? informationalVersion = null,
            string? nativeReadiness = null)
        {
            ProductName = productName;
            ProductSubtitle = productSubtitle;
            ReleaseVersion = FormatReleaseVersion(assemblyVersion);
            ShortHash = ExtractShortHash(informationalVersion, ReleaseVersion);
            RuntimeLine = $"{RuntimeInformation.FrameworkDescription} · "
                + $"{RuntimeInformation.OSDescription} · "
                + $"{RuntimeInformation.ProcessArchitecture}";
            NativeReadinessLine = string.IsNullOrWhiteSpace(nativeReadiness)
                ? "Not checked"
                : nativeReadiness;
        }

        /// <summary>
        /// Convenience overload carrying only the informational version:
        /// no assembly version is available, so the release degrades to
        /// "Unknown" and the short hash is extracted from the supplied
        /// informational version exactly as in the main constructor.
        /// </summary>
        public AboutWindowViewModel(string productName, string productSubtitle, string informationalVersion)
            : this(productName, productSubtitle, null, informationalVersion)
        {
        }

        /// <summary>
        /// The product name, e.g. "Digital Voice Modem".
        /// </summary>
        public string ProductName { get; }

        /// <summary>
        /// The product subtitle, e.g. "Desktop Dispatch Console".
        /// </summary>
        public string ProductSubtitle { get; }

        /// <summary>
        /// The release line, e.g. "R01A02", or "Unknown" when no
        /// assembly version is available.
        /// </summary>
        public string ReleaseVersion { get; }

        /// <summary>
        /// The short commit hash (first 7 characters), or "unknown".
        /// </summary>
        public string ShortHash { get; }

        /// <summary>
        /// The managed runtime, operating system and process architecture
        /// reported by the packaged process.
        /// </summary>
        public string RuntimeLine { get; }

        /// <summary>
        /// Startup native-vocoder readiness, or "Not checked" when the
        /// view-model is constructed outside the application composition root.
        /// </summary>
        public string NativeReadinessLine { get; }

        /// <summary>
        /// The combined version line, e.g. "R01A02 (abcdef1)".
        /// </summary>
        public string VersionLine => $"{ReleaseVersion} ({ShortHash})";

        /// <summary>
        /// The AGPL license notice sentence.
        /// </summary>
        public string LicenseLine => LicenseNotice;

        /// <summary>
        /// The license URL.
        /// </summary>
        public string LicenseUrl => LicenseLink;

        /// <summary>
        /// The source repository URL.
        /// </summary>
        public string RepositoryUrl => RepositoryLink;

        /// <summary>
        /// The external documentation URL used by packaged builds.
        /// </summary>
        public string DocumentationUrl => DocumentationLink;

        /// <summary>
        /// Formats the RxxAyy release from the assembly version (WPF
        /// AboutWindow.xaml.cs parity); "Unknown" when no version is
        /// supplied.
        /// </summary>
        internal static string FormatReleaseVersion(Version? assemblyVersion)
            => assemblyVersion is null
                ? "Unknown"
                : $"R{assemblyVersion.Major:D2}A{assemblyVersion.Minor:D2}";

        /// <summary>
        /// Extracts the short commit hash from the informational
        /// version, WPF AboutWindow.xaml.cs three-case parity. Degrades
        /// to "unknown" on malformed input and never throws.
        /// <para>
        /// Case 1: "R01A02 (abcdef123...)" — the hash inside the
        /// parentheses, capped at 7 characters. A lone or unclosed
        /// parenthesis is malformed and degrades to "unknown".
        /// </para>
        /// <para>
        /// Case 2: "R01A02+2919e2e..." — the hash from the
        /// +build-metadata, first dot-separated part, capped at 7.
        /// </para>
        /// <para>
        /// Case 3: space-separated fallback — the first token that is
        /// not the release version itself, capped at 7.
        /// </para>
        /// </summary>
        private static string ExtractShortHash(string? informationalVersion, string releaseVersion)
        {
            if (string.IsNullOrWhiteSpace(informationalVersion))
            {
                return "unknown";
            }

            int openParen = informationalVersion.IndexOf('(');
            int closeParen = informationalVersion.IndexOf(')');

            // Case 1: parenthesized hash. Any parenthesis without a
            // well-formed pair is malformed input: degrade to "unknown"
            // rather than falling through to the other cases.
            if (openParen >= 0 || closeParen >= 0)
            {
                if (openParen < 0 || closeParen <= openParen)
                {
                    return "unknown";
                }

                string inside = informationalVersion
                    .Substring(openParen + 1, closeParen - openParen - 1)
                    .Trim();
                return string.IsNullOrWhiteSpace(inside)
                    ? "unknown"
                    : CapAtSeven(inside);
            }

            // Case 2: +build metadata, e.g. "R01A02+2919e2e...".
            int plusIndex = informationalVersion.IndexOf('+');
            if (plusIndex >= 0 && plusIndex < informationalVersion.Length - 1)
            {
                string metadata = informationalVersion.Substring(plusIndex + 1).Trim();
                string firstPart = metadata.Split('.')[0].Trim();
                return string.IsNullOrWhiteSpace(firstPart)
                    ? "unknown"
                    : CapAtSeven(firstPart);
            }

            // Case 3: space-separated fallback — the first token that
            // is not the release version itself.
            string[] parts = informationalVersion.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1)
            {
                foreach (string part in parts)
                {
                    string candidate = part.Trim();
                    if (!string.IsNullOrWhiteSpace(candidate) && candidate != releaseVersion)
                    {
                        return CapAtSeven(candidate);
                    }
                }
            }

            return "unknown";
        }

        /// <summary>
        /// The first 7 characters, or the whole string when shorter.
        /// </summary>
        private static string CapAtSeven(string value)
            => value.Length > 7 ? value.Substring(0, 7) : value;
    }
}
