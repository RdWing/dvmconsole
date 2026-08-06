// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.IO;
using System.Reflection;
using DvmConsole.Platform.Native;
using Xunit;

namespace DvmConsole.Platform.Tests
{
    /// <summary>
    /// RED contract gate for the macOS app-bundle native-library resolver.
    /// These facts are host-independent; native loading and resolver
    /// registration remain production concerns.
    /// </summary>
    public sealed class MacBundleLibraryResolverTests
    {
        [Fact]
        public void Resolver_IsPublicStaticAndExposesRegistrationSurface()
        {
            var resolverType = typeof(MacBundleLibraryResolver);

            Assert.True(resolverType.IsAbstract);
            Assert.True(resolverType.IsSealed);

            var register = resolverType.GetMethod(
                nameof(MacBundleLibraryResolver.Register),
                BindingFlags.Public | BindingFlags.Static);

            Assert.NotNull(register);
            var parameters = register!.GetParameters();
            Assert.Single(parameters);
            Assert.Equal(typeof(Assembly), parameters[0].ParameterType);
        }

        [Fact]
        public void FindBundleRoot_RecognizesContentsMacOsDirectory()
        {
            var bundleRoot = BundleRoot();
            var baseDirectory = Path.Combine(bundleRoot, "Contents", "MacOS") + Path.DirectorySeparatorChar;

            Assert.Equal(bundleRoot, MacBundleLibraryResolver.FindBundleRoot(baseDirectory));
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        public void FindBundleRoot_BlankDirectoryReturnsNull(string baseDirectory)
        {
            Assert.Null(MacBundleLibraryResolver.FindBundleRoot(baseDirectory));
        }

        [Fact]
        public void FindBundleRoot_MalformedDirectoriesReturnNull()
        {
            var bundleRoot = BundleRoot();

            Assert.Null(MacBundleLibraryResolver.FindBundleRoot(
                Path.Combine(bundleRoot, "Contents")));
            Assert.Null(MacBundleLibraryResolver.FindBundleRoot(
                Path.Combine(bundleRoot, "Contents", "Other")));
            Assert.Null(MacBundleLibraryResolver.FindBundleRoot(
                Path.Combine(bundleRoot, "Contents", "MacOS", "nested")));
            Assert.Null(MacBundleLibraryResolver.FindBundleRoot(
                Path.Combine(bundleRoot, "Other", "MacOS")));
            Assert.Null(MacBundleLibraryResolver.FindBundleRoot(null));
        }

        [Fact]
        public void ResolveLibraryPath_OnMacOs_MapsLibvocoderIntoFrameworks()
        {
            var bundleRoot = BundleRoot();
            var baseDirectory = Path.Combine(bundleRoot, "Contents", "MacOS");
            var expected = Path.Combine(bundleRoot, "Contents", "Frameworks", "libvocoder.dylib");

            Assert.Equal(
                expected,
                MacBundleLibraryResolver.ResolveLibraryPath(
                    "libvocoder",
                    baseDirectory,
                    isMacOS: true));
        }

        [Fact]
        public void ResolveLibraryPath_OffMacOsFallsThroughToDefaultLoader()
        {
            Assert.Null(MacBundleLibraryResolver.ResolveLibraryPath(
                "libvocoder",
                Path.Combine(BundleRoot(), "Contents", "MacOS"),
                isMacOS: false));
        }

        [Fact]
        public void ResolveLibraryPath_WrongLogicalNameFallsThroughUnchanged()
        {
            Assert.Null(MacBundleLibraryResolver.ResolveLibraryPath(
                "CoreAudio",
                Path.Combine(BundleRoot(), "Contents", "MacOS"),
                isMacOS: true));
        }

        [Fact]
        public void ResolveLibraryPath_CaseVariantOfLibvocoderFallsThroughUnchanged()
        {
            // The mapping is literal: only the exact name "libvocoder"
            // maps to the packaged dylib; any case variant must fall
            // through to the default loader untouched.
            Assert.Null(MacBundleLibraryResolver.ResolveLibraryPath(
                "LIBVOCODER",
                Path.Combine(BundleRoot(), "Contents", "MacOS"),
                isMacOS: true));
        }

        [Fact]
        public void ResolveLibraryPath_NullOrMalformedRootReturnsNull()
        {
            Assert.Null(MacBundleLibraryResolver.ResolveLibraryPath(
                "libvocoder",
                null,
                isMacOS: true));
            Assert.Null(MacBundleLibraryResolver.ResolveLibraryPath(
                "libvocoder",
                Path.Combine(BundleRoot(), "NotMacOs"),
                isMacOS: true));
        }

        private static string BundleRoot()
            => Path.Combine(Path.GetTempPath(), "DvmConsole-ResolverTests.app");
    }
}
