// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DvmConsole.Platform.Native
{
    /// <summary>
    /// Resolves the packaged <c>libvocoder.dylib</c> inside a macOS
    /// <c>.app</c> bundle. It registers a <see cref="DllImportResolver"/>
    /// with <see cref="NativeLibrary.SetDllImportResolver"/> for
    /// <c>DvmConsole.Platform</c>, so DllImport-based consumers in that
    /// assembly are offered the bundle candidate before the OS loader.
    /// The startup readiness probe does not rely on this resolver: it maps
    /// and loads the bundle candidate directly (see
    /// <see cref="NativeLibraryProbe"/>), because
    /// <see cref="NativeLibrary.TryLoad(string, Assembly, DllImportSearchPath?, out IntPtr)"/>
    /// does not itself invoke the registered resolver in the actual macOS
    /// runtime path. Registration remains the correct mechanism for
    /// DllImport consumers and is still performed by the app.
    ///
    /// The mapping is deliberately narrow and pure: on macOS only the
    /// logical name <c>libvocoder</c> combined with a process base directory
    /// whose final path components are <c>Contents/MacOS</c> maps to
    /// <c>&lt;bundle&gt;/Contents/Frameworks/libvocoder.dylib</c>. Every
    /// other name, host or base directory falls through to the default
    /// loader unchanged, so un-packaged development runs are unaffected.
    /// The mapping never checks the filesystem — it is pure path mapping;
    /// the loader decides whether the candidate actually loads.
    ///
    /// The class is BCL-only and headless-testable: no P/Invoke, no
    /// Avalonia, no host-specific types.
    /// </summary>
    public static class MacBundleLibraryResolver
    {
        /*
        ** Fields
        */

        private static readonly object RegistrationGate = new();
        private static readonly HashSet<Assembly> RegisteredAssemblies = new();

        /*
        ** Methods
        */

        /// <summary>
        /// Registers the packaged-bundle resolver for the supplied assembly,
        /// so loads issued for that assembly (logical name
        /// <c>libvocoder</c>) are offered the bundle candidate first.
        /// macOS-only: on any other host this is a no-op and normal runtime
        /// resolution is untouched. Calling this more than once for the same
        /// assembly is safe — later calls are ignored, since the runtime
        /// forbids replacing an already-registered resolver.
        /// </summary>
        /// <param name="assembly">The assembly whose loads must consult the
        /// resolver.</param>
        /// <exception cref="ArgumentNullException">When
        /// <paramref name="assembly"/> is null.</exception>
        public static void Register(Assembly assembly)
        {
            ArgumentNullException.ThrowIfNull(assembly);

            if (!OperatingSystem.IsMacOS())
            {
                return;
            }

            lock (RegistrationGate)
            {
                if (!RegisteredAssemblies.Add(assembly))
                {
                    return;
                }
            }

            NativeLibrary.SetDllImportResolver(assembly, ResolveDllImport);
        }

        /// <summary>
        /// Recognizes a base directory whose final path components are
        /// <c>Contents/MacOS</c> (a trailing separator is accepted) and
        /// returns the bundle root — the directory that contains
        /// <c>Contents</c>. Returns null for null/blank input, for paths
        /// without the exact <c>Contents/MacOS</c> tail, and for nested
        /// paths that continue past <c>MacOS</c>. Pure string mapping: the
        /// path is never checked against the filesystem, so a bundle that
        /// does not exist yet is still recognized.
        /// </summary>
        /// <param name="baseDirectory">Candidate process base directory,
        /// e.g. <see cref="AppContext.BaseDirectory"/> of a packaged
        /// app.</param>
        /// <returns>The bundle root path, or null when the base directory
        /// is not a macOS bundle executable directory.</returns>
        public static string? FindBundleRoot(string? baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                return null;
            }

            // Accept ".../Contents/MacOS/" with a trailing separator, but
            // never deeper nesting: after trimming, the final component must
            // still be "MacOS" itself.
            string macOsDirectory = baseDirectory.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (macOsDirectory.Length == 0)
            {
                return null;
            }

            if (!string.Equals(Path.GetFileName(macOsDirectory), "MacOS", StringComparison.Ordinal))
            {
                return null;
            }

            string? contentsDirectory = Path.GetDirectoryName(macOsDirectory);
            if (contentsDirectory == null
                || !string.Equals(Path.GetFileName(contentsDirectory), "Contents", StringComparison.Ordinal))
            {
                return null;
            }

            string? bundleRoot = Path.GetDirectoryName(contentsDirectory);
            return string.IsNullOrEmpty(bundleRoot) ? null : bundleRoot;
        }

        /// <summary>
        /// Maps the logical <c>libvocoder</c> name to its packaged bundle
        /// path on macOS: <c>&lt;bundle&gt;/Contents/Frameworks/libvocoder.dylib</c>.
        /// Returns null off macOS, for every other logical name, and for
        /// null or malformed base directories, so normal runtime resolution
        /// is unchanged in all those cases. Pure path mapping — the
        /// candidate path is never checked for existence here.
        /// </summary>
        /// <param name="logicalName">Logical library name; only the exact
        /// literal name <c>libvocoder</c> is mapped — case variants fall
        /// through.</param>
        /// <param name="baseDirectory">Process base directory whose final
        /// path components must be <c>Contents/MacOS</c>.</param>
        /// <param name="isMacOS">True when the host is macOS.</param>
        /// <returns>The packaged dylib path, or null when no mapping
        /// applies.</returns>
        public static string? ResolveLibraryPath(string? logicalName, string? baseDirectory, bool isMacOS)
        {
            if (!isMacOS)
            {
                return null;
            }

            if (!string.Equals(logicalName, VocoderReadiness.LogicalLibraryName, StringComparison.Ordinal))
            {
                return null;
            }

            string? bundleRoot = FindBundleRoot(baseDirectory);
            if (bundleRoot == null)
            {
                return null;
            }

            return Path.Combine(bundleRoot, "Contents", "Frameworks", "libvocoder.dylib");
        }

        /*
        ** Helpers
        */

        /// <summary>
        /// The <see cref="DllImportResolver"/> installed by
        /// <see cref="Register(Assembly)"/>. macOS-only; maps only the
        /// packaged <c>libvocoder</c> name to the bundle candidate and
        /// returns <see cref="IntPtr.Zero"/> for every other case so the
        /// runtime continues with normal resolution. The candidate is
        /// actually loaded here (not merely path-mapped); when it cannot be
        /// loaded, <see cref="IntPtr.Zero"/> again defers to the default
        /// loader.
        /// </summary>
        private static IntPtr ResolveDllImport(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (!OperatingSystem.IsMacOS())
            {
                return IntPtr.Zero;
            }

            string? candidate = ResolveLibraryPath(libraryName, AppContext.BaseDirectory, isMacOS: true);
            if (candidate == null)
            {
                return IntPtr.Zero;
            }

            return NativeLibrary.TryLoad(candidate, out IntPtr handle) ? handle : IntPtr.Zero;
        }
    }
}
