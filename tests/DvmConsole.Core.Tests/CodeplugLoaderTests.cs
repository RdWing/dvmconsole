// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
/**
* RED contract gate for the headless codeplug loader slice
* (plan vertical-slice gate item 2: load the existing YAML codeplug
* without changing its schema):
*
*   DvmConsole.Core.Configuration.CodeplugLoader
*   DvmConsole.Core.Configuration.CodeplugLoadResult
*
* The loader parses a dvmconsole codeplug YAML document with the exact
* production deserializer configuration the WPF app uses
* (MainWindow.xaml.cs LoadCodeplug): CamelCaseNamingConvention +
* IgnoreUnmatchedProperties, then NormalizeGroups() — but it NEVER
* throws. Missing files, malformed YAML, and empty documents all
* produce a typed result (FileMissing / failed with ErrorMessage);
* only a successful parse yields a Codeplug. LoadFromText is the
* headless test seam; LoadFromFile wraps it with a filesystem probe.
*/
using System;
using System.IO;
using System.Linq;
using dvmconsole;
using DvmConsole.Core.Configuration;
using Xunit;

namespace DvmConsole.Core.Tests
{
    /// <summary>
    /// RED contract gate for <see cref="CodeplugLoader"/>.
    /// </summary>
    public sealed class CodeplugLoaderTests
    {
        private const string MinimalCodeplugYaml = @"# SPDX-License-Identifier: AGPL-3.0-only
keyFile: fixtures/test-key.yml

systems:
  - name: Simplex Repeater
    identity: 310001
    address: 127.0.0.1
    port: 31000
    password: placeholder-password
    encrypted: false
    peerId: 310000
    rid: ""3100001""
    aliasPath: ./fixtures/alias.yml
    ridAlias:
      - alias: Test Radio
        rid: 3100001

zones:
  - name: Zone A
    tabColor: ""#1a2b3c""
    tabTextColor: ""#ffffff""
    channels:
      - name: CH 1 Minimal
        system: Simplex Repeater
        tgid: ""31001""
        slot: 1
        mode: dmr
        algo: none

groups:
  - name: Dispatch
    type: multiselect
  - name: Tac 1
    type: patch

patchSourceIdPassthrough: true
";

        [Fact]
        public void LoadFromText_ValidCodeplug_SucceedsWithSystems()
        {
            var result = CodeplugLoader.LoadFromText(MinimalCodeplugYaml);

            Assert.True(result.Succeeded);
            Assert.False(result.FileMissing);
            Assert.Null(result.ErrorMessage);
            Assert.NotNull(result.Codeplug);
            Assert.Single(result.Codeplug!.Systems);
            var system = result.Codeplug.Systems[0];
            Assert.Equal("Simplex Repeater", system.Name);
            Assert.Equal("127.0.0.1", system.Address);
            Assert.Equal(31000, system.Port);
            Assert.Equal(310000u, system.PeerId);
        }

        [Fact]
        public void LoadFromText_AppliesNormalizeGroups()
        {
            var yaml = MinimalCodeplugYaml + @"
patchGroups:
  - name: Tac 1
    type: """"
  - name: Legacy Patch
    type: ""  PATCH  ""
";

            var result = CodeplugLoader.LoadFromText(yaml);

            Assert.True(result.Succeeded);
            Assert.NotNull(result.Codeplug);
            // Tac 1 is declared in groups: as type "patch"; the legacy
            // patchGroups collision must not override it (current wins).
            Assert.Contains(result.Codeplug!.Groups, g => g.Name == "Tac 1" && g.Type == "patch");
            // Legacy Patch exists only in patchGroups; its padded type
            // normalizes to "patch" when merged into Groups.
            Assert.Contains(result.Codeplug.Groups, g => g.Name == "Legacy Patch" && g.Type == "patch");
        }

        [Fact]
        public void LoadFromText_MalformedYaml_FailedWithErrorMessage_NoThrow()
        {
            var result = CodeplugLoader.LoadFromText("systems: [unclosed");

            Assert.False(result.Succeeded);
            Assert.Null(result.Codeplug);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }

        [Fact]
        public void LoadFromText_EmptyOrWhitespace_Failed()
        {
            Assert.False(CodeplugLoader.LoadFromText("").Succeeded);
            Assert.False(CodeplugLoader.LoadFromText("   \n  \n").Succeeded);
        }

        [Fact]
        public void LoadFromText_NullScalarDocuments_FailedWithErrorMessage()
        {
            // YAML documents that deserialize to a null Codeplug (null
            // scalar, tilde, document marker, comment-only) must NOT report
            // success: the contract is "Succeeded implies a parsed
            // Codeplug", so they fail with an ErrorMessage instead.
            foreach (var yaml in new[] { "null", "~", "---", "# only a comment\n" })
            {
                var result = CodeplugLoader.LoadFromText(yaml);
                Assert.False(result.Succeeded);
                Assert.Null(result.Codeplug);
                Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
            }
        }

        [Fact]
        public void LoadFromText_NotYaml_FailedWithErrorMessage()
        {
            var result = CodeplugLoader.LoadFromText("this is not yaml at all [[[");

            Assert.False(result.Succeeded);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }

        [Fact]
        public void LoadFromFile_MissingPath_FileMissingResult_NoThrow()
        {
            var missing = Path.Combine(Path.GetTempPath(), "dvm-no-such-codeplug-" + Guid.NewGuid().ToString("N") + ".yml");

            var result = CodeplugLoader.LoadFromFile(missing);

            Assert.False(result.Succeeded);
            Assert.True(result.FileMissing);
            Assert.Null(result.Codeplug);
        }

        [Fact]
        public void LoadFromFile_RoundTripsTempFile()
        {
            var path = Path.Combine(Path.GetTempPath(), "dvm-codeplug-" + Guid.NewGuid().ToString("N") + ".yml");
            try
            {
                File.WriteAllText(path, MinimalCodeplugYaml);
                var result = CodeplugLoader.LoadFromFile(path);

                Assert.True(result.Succeeded);
                Assert.False(result.FileMissing);
                Assert.Single(result.Codeplug!.Systems);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void LoadFromFile_MalformedFile_FailedWithErrorMessage()
        {
            var path = Path.Combine(Path.GetTempPath(), "dvm-bad-codeplug-" + Guid.NewGuid().ToString("N") + ".yml");
            try
            {
                File.WriteAllText(path, "systems: [unclosed");
                var result = CodeplugLoader.LoadFromFile(path);

                Assert.False(result.Succeeded);
                Assert.False(result.FileMissing);
                Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void LoadFromFile_NullOrBlankPath_FileMissingResult()
        {
            Assert.True(CodeplugLoader.LoadFromFile(null!).FileMissing);
            Assert.True(CodeplugLoader.LoadFromFile("  ").FileMissing);
            Assert.True(CodeplugLoader.LoadFromFile(string.Empty).FileMissing);
        }

        [Fact]
        public void CodeplugLoadResult_SurfaceIsExact()
        {
            var type = typeof(CodeplugLoadResult);
            Assert.True(type.IsSealed);
            Assert.NotNull(type.GetProperty("Succeeded"));
            Assert.NotNull(type.GetProperty("Codeplug"));
            Assert.NotNull(type.GetProperty("ErrorMessage"));
            Assert.NotNull(type.GetProperty("FileMissing"));
        }

        [Fact]
        public void CodeplugLoader_SurfaceIsExact()
        {
            var type = typeof(CodeplugLoader);
            Assert.True(type.IsSealed);
            var methods = type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            var names = methods.Select(m => m.Name).OrderBy(n => n).ToArray();
            Assert.Contains("LoadFromFile", names);
            Assert.Contains("LoadFromText", names);
        }
    }
}
