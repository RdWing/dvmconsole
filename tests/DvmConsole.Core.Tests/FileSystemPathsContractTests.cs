// SPDX-License-Identifier: AGPL-3.0-only
/**
* Deterministic compile-smoke contract tests for the production
* DvmConsole.Core/Configuration/IFileSystemPaths.cs and
* DvmConsole.Core/Configuration/DefaultFileSystemPaths.cs. These lock the
* path-composition contract (application root, settings file, TAR recordings
* folder, trace log and alias directories) that SettingsManager and the WPF
* app depend on. Bases are injected as /tmp-style hermetic paths so the tests
* never touch real user folders, except the explicit environment-fallback
* test.
*/
using dvmconsole;
using Xunit;

namespace DvmConsole.Core.Tests
{
    /// <summary>
    /// Compile-smoke contract tests for <see cref="IFileSystemPaths"/> and
    /// <see cref="DefaultFileSystemPaths"/>.
    /// </summary>
    public class FileSystemPathsContractTests
    {
        private static readonly string AppDataBase = Path.Combine(Path.GetTempPath(), "dvmconsole-test-appdata");
        private static readonly string DocumentsBase = Path.Combine(Path.GetTempPath(), "dvmconsole-test-documents");
        private static readonly string OverrideRoot = Path.Combine(Path.GetTempPath(), "dvmconsole-test-override");

        /// <summary>
        /// Injected bases compose the DVMProject/dvmconsole application root
        /// and the DVMConsole/TAR recordings root.
        /// </summary>
        [Fact]
        public void InjectedBases_ComposeExpectedRoots()
        {
            var paths = new DefaultFileSystemPaths(AppDataBase, DocumentsBase);

            Assert.Equal(Path.Combine(AppDataBase, "DVMProject", "dvmconsole"), paths.ApplicationDataRootPath);
            Assert.Equal(Path.Combine(DocumentsBase, "DVMConsole", "TAR"), paths.DefaultTarRecordingsPath);
        }

        /// <summary>
        /// The settings file lives directly under the application root.
        /// </summary>
        [Fact]
        public void SettingsFilePath_IsUnderApplicationRoot()
        {
            var paths = new DefaultFileSystemPaths(AppDataBase, DocumentsBase);

            Assert.Equal(Path.Combine(paths.ApplicationDataRootPath, "UserSettings.json"), paths.SettingsFilePath);
        }

        /// <summary>
        /// A non-empty profile override replaces the application root (and the
        /// settings file derived from it) exactly, while the TAR recordings
        /// path stays anchored to the documents base.
        /// </summary>
        [Fact]
        public void Override_ChangesRootAndSettingsOnly()
        {
            var paths = new DefaultFileSystemPaths(AppDataBase, DocumentsBase, OverrideRoot);

            Assert.Equal(OverrideRoot, paths.ApplicationDataRootPath);
            Assert.Equal(Path.Combine(OverrideRoot, "UserSettings.json"), paths.SettingsFilePath);
            Assert.Equal(Path.Combine(DocumentsBase, "DVMConsole", "TAR"), paths.DefaultTarRecordingsPath);
        }

        /// <summary>
        /// string.Empty is the legacy App.USER_PROFILE_PATH_OVERRIDE "no
        /// override" sentinel: it must behave exactly like omitting the
        /// override (null) and like not passing it at all.
        /// </summary>
        [Fact]
        public void EmptyOverride_EqualsNoOverride()
        {
            var noOverride = new DefaultFileSystemPaths(AppDataBase, DocumentsBase);
            var emptyOverride = new DefaultFileSystemPaths(AppDataBase, DocumentsBase, string.Empty);
            var nullOverride = new DefaultFileSystemPaths(AppDataBase, DocumentsBase, null);

            Assert.Equal(noOverride.ApplicationDataRootPath, emptyOverride.ApplicationDataRootPath);
            Assert.Equal(noOverride.SettingsFilePath, emptyOverride.SettingsFilePath);
            Assert.Equal(noOverride.DefaultTarRecordingsPath, emptyOverride.DefaultTarRecordingsPath);
            Assert.Equal(noOverride.TraceLogDirectoryPath, emptyOverride.TraceLogDirectoryPath);
            Assert.Equal(noOverride.DefaultAliasDirectoryPath, emptyOverride.DefaultAliasDirectoryPath);
            Assert.Equal(noOverride.ApplicationDataRootPath, nullOverride.ApplicationDataRootPath);
        }

        /// <summary>
        /// The trace log directory is the application root itself and is
        /// rooted (never relative).
        /// </summary>
        [Fact]
        public void TraceLogDirectory_IsRootAndRooted()
        {
            var paths = new DefaultFileSystemPaths(AppDataBase, DocumentsBase);

            Assert.Equal(paths.ApplicationDataRootPath, paths.TraceLogDirectoryPath);
            Assert.True(Path.IsPathRooted(paths.TraceLogDirectoryPath));
        }

        /// <summary>
        /// The default alias directory is the application root.
        /// </summary>
        [Fact]
        public void AliasDirectory_IsRoot()
        {
            var paths = new DefaultFileSystemPaths(AppDataBase, DocumentsBase);

            Assert.Equal(paths.ApplicationDataRootPath, paths.DefaultAliasDirectoryPath);
        }

        /// <summary>
        /// Null bases fall back to the real environment folders; the composed
        /// paths still carry the expected segments and are rooted.
        /// </summary>
        [Fact]
        public void NullBases_FallBackToEnvironmentFolders()
        {
            var paths = new DefaultFileSystemPaths();

            string[] rootSegments = paths.ApplicationDataRootPath.Split(Path.DirectorySeparatorChar);
            Assert.Contains("DVMProject", rootSegments);
            Assert.Contains("dvmconsole", rootSegments);
            Assert.True(Path.IsPathRooted(paths.ApplicationDataRootPath));

            string[] tarSegments = paths.DefaultTarRecordingsPath.Split(Path.DirectorySeparatorChar);
            Assert.Contains("DVMConsole", tarSegments);
            Assert.Contains("TAR", tarSegments);
            Assert.True(Path.IsPathRooted(paths.DefaultTarRecordingsPath));

            Assert.Equal("UserSettings.json", Path.GetFileName(paths.SettingsFilePath));
            Assert.True(Path.IsPathRooted(paths.SettingsFilePath));
        }

        /// <summary>
        /// The contract lives in the portable DvmConsole.Core assembly, not in
        /// the WPF app or fnecore.
        /// </summary>
        [Fact]
        public void Interface_AssemblyIsDvmConsoleCore()
        {
            Assert.Equal("DvmConsole.Core", typeof(IFileSystemPaths).Assembly.GetName().Name);
        }

        /// <summary>
        /// The interface exposes exactly the five read-only string properties
        /// of the path contract; adding or mutating any of them breaks the
        /// contract.
        /// </summary>
        [Fact]
        public void Interface_ExposesExactlyFiveReadOnlyStringProperties()
        {
            var properties = typeof(IFileSystemPaths).GetProperties();

            Assert.Equal(5, properties.Length);
            Assert.All(properties, p =>
            {
                Assert.Equal(typeof(string), p.PropertyType);
                Assert.NotNull(p.GetMethod);
                Assert.True(p.GetMethod.IsPublic);
                Assert.Null(p.SetMethod);
            });

            string[] names = properties.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(
                new[]
                {
                    "ApplicationDataRootPath",
                    "DefaultAliasDirectoryPath",
                    "DefaultTarRecordingsPath",
                    "SettingsFilePath",
                    "TraceLogDirectoryPath"
                },
                names);
        }

        /// <summary>
        /// The default implementation is sealed, implements the contract, and
        /// exposes no settable state (immutable after construction).
        /// </summary>
        [Fact]
        public void DefaultFileSystemPaths_IsSealedImmutableImplementation()
        {
            Assert.True(typeof(IFileSystemPaths).IsAssignableFrom(typeof(DefaultFileSystemPaths)));
            Assert.True(typeof(DefaultFileSystemPaths).IsSealed);

            var paths = new DefaultFileSystemPaths(AppDataBase, DocumentsBase);
            Assert.All(typeof(DefaultFileSystemPaths).GetProperties(), p => Assert.Null(p.SetMethod));
        }
    }
}
