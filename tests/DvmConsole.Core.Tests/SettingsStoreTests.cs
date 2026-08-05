// SPDX-License-Identifier: AGPL-3.0-only
/**
* Deterministic compile-smoke contract tests for the production
* DvmConsole.Core/Configuration/ISettingsStore.cs and
* DvmConsole.Core/Configuration/JsonSettingsStore.cs. These lock the generic
* JSON settings-store contract (exists probe, typed load, save, delete) that
* SettingsManager and the WPF app will route through. Every test is hermetic:
* each test gets a fresh per-test temp directory that is deleted afterwards,
* and all payloads are plain POCOs (no WPF SettingsManager).
*/
using dvmconsole;
using Newtonsoft.Json;
using System.Reflection;
using Xunit;

namespace DvmConsole.Core.Tests
{
    /// <summary>
    /// Compile-smoke contract tests for <see cref="ISettingsStore"/> and
    /// <see cref="JsonSettingsStore"/>.
    /// </summary>
    public class SettingsStoreTests
    {
        /// <summary>
        /// Hermetic per-test temp directory; deleted on dispose.
        /// </summary>
        private sealed class TempDir : IDisposable
        {
            public string Root { get; }

            public TempDir()
            {
                Root = Path.Combine(Path.GetTempPath(), "dvmconsole-settingsstore-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Root);
            }

            /// <summary>
            /// A store path nested two levels deep, so saves must create the
            /// parent directory chain.
            /// </summary>
            public string NewNestedStorePath()
            {
                return Path.Combine(Root, "nested", "deep", "UserSettings.json");
            }

            /// <summary>
            /// A store path directly under the temp root.
            /// </summary>
            public string NewStorePath()
            {
                return Path.Combine(Root, "UserSettings.json");
            }

            public void Dispose()
            {
                try
                {
                    if (Directory.Exists(Root))
                        Directory.Delete(Root, true);
                }
                catch (IOException)
                {
                    // Best-effort cleanup; never mask the test result.
                }
            }
        }

        /// <summary>
        /// Plain POCO payload exercising every supported JSON shape: null and
        /// non-ASCII strings, bool, int, double, list, dictionary, nested
        /// object, and a <see cref="JsonIgnore"/>-d member.
        /// </summary>
        private sealed class SettingsModel
        {
            public string DisplayName { get; set; }
            public string NullableNote { get; set; }
            public bool IsEnabled { get; set; }
            public int ChannelCount { get; set; }
            public double SquelchLevel { get; set; }
            public List<string> Frequencies { get; set; }
            public Dictionary<string, int> Talkgroups { get; set; }
            public NestedSettings Nested { get; set; }

            [JsonIgnore]
            public string ComputedTag { get; set; }
        }

        private sealed class NestedSettings
        {
            public string Label { get; set; }
            public int Priority { get; set; }
        }

        private static SettingsModel CreateFullModel()
        {
            return new SettingsModel
            {
                DisplayName = "Café ☕ 日本語 🚀 Console",
                NullableNote = null,
                IsEnabled = true,
                ChannelCount = 42,
                SquelchLevel = 3.14159,
                Frequencies = new List<string> { "145.5000", "146.5200", null },
                Talkgroups = new Dictionary<string, int> { { "Alpha", 1 }, { "Bravo", 2 } },
                Nested = new NestedSettings { Label = "Nested", Priority = 7 }
            };
        }

        /// <summary>
        /// The contract lives in the portable DvmConsole.Core assembly in the
        /// dvmconsole namespace, not in the WPF app or fnecore.
        /// </summary>
        [Fact]
        public void Interface_AssemblyIsDvmConsoleCore()
        {
            Assert.Equal("DvmConsole.Core", typeof(ISettingsStore).Assembly.GetName().Name);
            Assert.Equal("dvmconsole", typeof(ISettingsStore).Namespace);
            Assert.Equal("dvmconsole", typeof(JsonSettingsStore).Namespace);
        }

        /// <summary>
        /// The interface exposes exactly the four contract members: read-only
        /// Exists, generic TryLoad&lt;T&gt; (class-constrained, out parameter,
        /// bool result), generic Save&lt;T&gt;, and parameterless Delete.
        /// </summary>
        [Fact]
        public void Interface_ExposesExactlyFourMembers()
        {
            var properties = typeof(ISettingsStore).GetProperties();
            Assert.Single(properties);
            Assert.Equal("Exists", properties[0].Name);
            Assert.Equal(typeof(bool), properties[0].PropertyType);
            Assert.NotNull(properties[0].GetMethod);
            Assert.True(properties[0].GetMethod.IsPublic);
            Assert.Null(properties[0].SetMethod);

            var methods = typeof(ISettingsStore).GetMethods().Where(m => !m.IsSpecialName).ToList();
            Assert.Equal(3, methods.Count);

            MethodInfo delete = methods.Single(m => m.Name == "Delete");
            Assert.Empty(delete.GetParameters());
            Assert.Equal(typeof(void), delete.ReturnType);

            MethodInfo save = methods.Single(m => m.Name == "Save");
            Assert.True(save.IsGenericMethodDefinition);
            Assert.Equal(typeof(void), save.ReturnType);
            var saveParam = Assert.Single(save.GetParameters());
            Assert.True(saveParam.ParameterType.IsGenericParameter);

            MethodInfo tryLoad = methods.Single(m => m.Name == "TryLoad");
            Assert.True(tryLoad.IsGenericMethodDefinition);
            Assert.Equal(typeof(bool), tryLoad.ReturnType);
            var loadParam = Assert.Single(tryLoad.GetParameters());
            Assert.True(loadParam.ParameterType.IsByRef);
            Assert.True(loadParam.IsOut);

            Type typeParam = tryLoad.GetGenericArguments()[0];
            Assert.True(typeParam.GenericParameterAttributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint));
        }

        /// <summary>
        /// The concrete store is public and sealed, implements the contract,
        /// and exposes no mutable public state (path is fixed at construction).
        /// </summary>
        [Fact]
        public void JsonSettingsStore_IsSealedPublicImmutableImplementation()
        {
            Assert.True(typeof(ISettingsStore).IsAssignableFrom(typeof(JsonSettingsStore)));
            Assert.True(typeof(JsonSettingsStore).IsSealed);
            Assert.True(typeof(JsonSettingsStore).IsPublic);
            Assert.All(typeof(JsonSettingsStore).GetProperties(), p => Assert.Null(p.SetMethod));
            Assert.Empty(typeof(JsonSettingsStore).GetFields(BindingFlags.Public | BindingFlags.Instance));
        }

        /// <summary>
        /// A null path is rejected at construction.
        /// </summary>
        [Fact]
        public void Constructor_NullPath_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => new JsonSettingsStore(null));
        }

        /// <summary>
        /// An empty path is rejected at construction.
        /// </summary>
        [Fact]
        public void Constructor_EmptyPath_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => new JsonSettingsStore(string.Empty));
        }

        /// <summary>
        /// A whitespace-only path is rejected at construction.
        /// </summary>
        [Fact]
        public void Constructor_WhitespacePath_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => new JsonSettingsStore("   "));
        }

        /// <summary>
        /// Exists is false for a file that has never been written.
        /// </summary>
        [Fact]
        public void Exists_MissingFile_ReturnsFalse()
        {
            using var dir = new TempDir();
            var store = new JsonSettingsStore(dir.NewStorePath());

            Assert.False(store.Exists);
        }

        /// <summary>
        /// Exists is true once a file has been saved.
        /// </summary>
        [Fact]
        public void Exists_PresentFile_ReturnsTrue()
        {
            using var dir = new TempDir();
            var store = new JsonSettingsStore(dir.NewStorePath());

            store.Save(new SettingsModel { DisplayName = "Probe" });

            Assert.True(store.Exists);
        }

        /// <summary>
        /// Exists flips back to false after Delete.
        /// </summary>
        [Fact]
        public void Exists_AfterDelete_ReturnsFalse()
        {
            using var dir = new TempDir();
            var store = new JsonSettingsStore(dir.NewStorePath());

            store.Save(new SettingsModel { DisplayName = "Probe" });
            Assert.True(store.Exists);

            store.Delete();
            Assert.False(store.Exists);
        }

        /// <summary>
        /// Null and non-ASCII strings survive a save/load round trip.
        /// </summary>
        [Fact]
        public void SaveThenLoad_RoundTripsNullAndNonAsciiStrings()
        {
            using var dir = new TempDir();
            var store = new JsonSettingsStore(dir.NewStorePath());

            store.Save(CreateFullModel());
            Assert.True(store.TryLoad<SettingsModel>(out SettingsModel loaded));
            Assert.NotNull(loaded);
            Assert.Equal("Café ☕ 日本語 🚀 Console", loaded.DisplayName);
            Assert.Null(loaded.NullableNote);
        }

        /// <summary>
        /// Bool, int and double values survive a save/load round trip.
        /// </summary>
        [Fact]
        public void SaveThenLoad_RoundTripsBoolIntDouble()
        {
            using var dir = new TempDir();
            var store = new JsonSettingsStore(dir.NewStorePath());

            store.Save(CreateFullModel());
            Assert.True(store.TryLoad<SettingsModel>(out SettingsModel loaded));
            Assert.NotNull(loaded);
            Assert.True(loaded.IsEnabled);
            Assert.Equal(42, loaded.ChannelCount);
            Assert.Equal(3.14159, loaded.SquelchLevel);
        }

        /// <summary>
        /// A list (including a null element) survives a save/load round trip.
        /// </summary>
        [Fact]
        public void SaveThenLoad_RoundTripsList()
        {
            using var dir = new TempDir();
            var store = new JsonSettingsStore(dir.NewStorePath());

            store.Save(CreateFullModel());
            Assert.True(store.TryLoad<SettingsModel>(out SettingsModel loaded));
            Assert.NotNull(loaded);
            Assert.Equal(new List<string> { "145.5000", "146.5200", null }, loaded.Frequencies);
        }

        /// <summary>
        /// A string-to-int dictionary survives a save/load round trip.
        /// </summary>
        [Fact]
        public void SaveThenLoad_RoundTripsDictionary()
        {
            using var dir = new TempDir();
            var store = new JsonSettingsStore(dir.NewStorePath());

            store.Save(CreateFullModel());
            Assert.True(store.TryLoad<SettingsModel>(out SettingsModel loaded));
            Assert.NotNull(loaded);
            Assert.Equal(2, loaded.Talkgroups.Count);
            Assert.Equal(1, loaded.Talkgroups["Alpha"]);
            Assert.Equal(2, loaded.Talkgroups["Bravo"]);
        }

        /// <summary>
        /// A nested object survives a save/load round trip.
        /// </summary>
        [Fact]
        public void SaveThenLoad_RoundTripsNestedObject()
        {
            using var dir = new TempDir();
            var store = new JsonSettingsStore(dir.NewStorePath());

            store.Save(CreateFullModel());
            Assert.True(store.TryLoad<SettingsModel>(out SettingsModel loaded));
            Assert.NotNull(loaded);
            Assert.NotNull(loaded.Nested);
            Assert.Equal("Nested", loaded.Nested.Label);
            Assert.Equal(7, loaded.Nested.Priority);
        }

        /// <summary>
        /// Save creates the full parent directory chain when it does not
        /// exist yet.
        /// </summary>
        [Fact]
        public void Save_CreatesParentDirectories()
        {
            using var dir = new TempDir();
            var store = new JsonSettingsStore(dir.NewNestedStorePath());

            store.Save(new SettingsModel { DisplayName = "Probe" });

            Assert.True(store.Exists);
            Assert.True(Directory.Exists(Path.Combine(dir.Root, "nested", "deep")));
        }

        /// <summary>
        /// Saved JSON is indented, PascalCase, and free of camelCase keys.
        /// </summary>
        [Fact]
        public void SavedJson_IsIndentedPascalCase()
        {
            using var dir = new TempDir();
            var store = new JsonSettingsStore(dir.NewStorePath());

            store.Save(CreateFullModel());
            string json = File.ReadAllText(dir.NewStorePath());

            Assert.Contains(Environment.NewLine, json);
            Assert.Contains("  \"DisplayName\"", json);
            Assert.Contains("\"IsEnabled\"", json);
            Assert.Contains("\"ChannelCount\"", json);
            Assert.DoesNotContain("displayName", json);
            Assert.DoesNotContain("isEnabled", json);
            Assert.DoesNotContain("channelCount", json);
        }

        /// <summary>
        /// Saved JSON carries no UTF-8 byte-order mark.
        /// </summary>
        [Fact]
        public void SavedJson_HasNoBom()
        {
            using var dir = new TempDir();
            var store = new JsonSettingsStore(dir.NewStorePath());

            store.Save(CreateFullModel());
            byte[] bytes = File.ReadAllBytes(dir.NewStorePath());

            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        }

        /// <summary>
        /// A [JsonIgnore] member is absent from the JSON and comes back as its
        /// default value after a load.
        /// </summary>
        [Fact]
        public void JsonIgnoreMember_AbsentInJsonAndDefaultAfterLoad()
        {
            using var dir = new TempDir();
            var store = new JsonSettingsStore(dir.NewStorePath());

            var model = CreateFullModel();
            model.ComputedTag = "must-not-be-serialized";
            store.Save(model);

            string json = File.ReadAllText(dir.NewStorePath());
            Assert.DoesNotContain("ComputedTag", json);
            Assert.DoesNotContain("must-not-be-serialized", json);

            Assert.True(store.TryLoad<SettingsModel>(out SettingsModel loaded));
            Assert.NotNull(loaded);
            Assert.Null(loaded.ComputedTag);
        }

        /// <summary>
        /// A missing file fails TryLoad with a null out value, without throwing.
        /// </summary>
        [Fact]
        public void TryLoad_MissingFile_ReturnsFalseWithNullOut()
        {
            using var dir = new TempDir();
            var store = new JsonSettingsStore(dir.NewStorePath());

            Assert.False(store.TryLoad<SettingsModel>(out SettingsModel settings));
            Assert.Null(settings);
        }

        /// <summary>
        /// Malformed JSON fails TryLoad with a null out value, without throwing.
        /// </summary>
        [Fact]
        public void TryLoad_MalformedJson_ReturnsFalseWithNullOut()
        {
            using var dir = new TempDir();
            var store = new JsonSettingsStore(dir.NewStorePath());

            File.WriteAllText(dir.NewStorePath(), "{ not json !!!");

            Assert.False(store.TryLoad<SettingsModel>(out SettingsModel settings));
            Assert.Null(settings);
        }

        /// <summary>
        /// An empty file fails TryLoad with a null out value, without throwing.
        /// </summary>
        [Fact]
        public void TryLoad_EmptyFile_ReturnsFalseWithNullOut()
        {
            using var dir = new TempDir();
            var store = new JsonSettingsStore(dir.NewStorePath());

            File.WriteAllText(dir.NewStorePath(), string.Empty);

            Assert.False(store.TryLoad<SettingsModel>(out SettingsModel settings));
            Assert.Null(settings);
        }

        /// <summary>
        /// A file whose content is the literal JSON null fails TryLoad with a
        /// null out value, without throwing.
        /// </summary>
        [Fact]
        public void TryLoad_LiteralNullJson_ReturnsFalseWithNullOut()
        {
            using var dir = new TempDir();
            var store = new JsonSettingsStore(dir.NewStorePath());

            File.WriteAllText(dir.NewStorePath(), "null");

            Assert.False(store.TryLoad<SettingsModel>(out SettingsModel settings));
            Assert.Null(settings);
        }

        /// <summary>
        /// A store path pointing at a directory fails TryLoad with a null out
        /// value, without throwing.
        /// </summary>
        [Fact]
        public void TryLoad_DirectoryPath_ReturnsFalseWithNullOut()
        {
            using var dir = new TempDir();
            Directory.CreateDirectory(dir.NewStorePath());
            var store = new JsonSettingsStore(dir.NewStorePath());

            Assert.False(store.TryLoad<SettingsModel>(out SettingsModel settings));
            Assert.Null(settings);
        }

        /// <summary>
        /// JSON that cannot be deserialized into the requested type fails
        /// TryLoad with a null out value, without throwing.
        /// </summary>
        [Fact]
        public void TryLoad_TypeMismatchJson_ReturnsFalseWithNullOut()
        {
            using var dir = new TempDir();
            var store = new JsonSettingsStore(dir.NewStorePath());

            File.WriteAllText(dir.NewStorePath(), "\"just a string, not an object\"");

            Assert.False(store.TryLoad<SettingsModel>(out SettingsModel settings));
            Assert.Null(settings);
        }

        /// <summary>
        /// A UTF-8 BOM-prefixed settings file loads successfully.
        /// </summary>
        [Fact]
        public void TryLoad_Utf8BomFixture_LoadsSuccessfully()
        {
            using var dir = new TempDir();
            var store = new JsonSettingsStore(dir.NewStorePath());

            byte[] bom = { 0xEF, 0xBB, 0xBF };
            byte[] json = System.Text.Encoding.UTF8.GetBytes("{\"DisplayName\":\"Bom Fixture\",\"IsEnabled\":true}");
            File.WriteAllBytes(dir.NewStorePath(), bom.Concat(json).ToArray());

            Assert.True(store.TryLoad<SettingsModel>(out SettingsModel settings));
            Assert.NotNull(settings);
            Assert.Equal("Bom Fixture", settings.DisplayName);
            Assert.True(settings.IsEnabled);
        }

        /// <summary>
        /// Saving twice overwrites the file so only the latest payload remains.
        /// </summary>
        [Fact]
        public void Save_Twice_LeavesOnlyLatestPayload()
        {
            using var dir = new TempDir();
            var store = new JsonSettingsStore(dir.NewStorePath());

            store.Save(new SettingsModel { DisplayName = "First", ChannelCount = 1 });
            store.Save(new SettingsModel { DisplayName = "Second", ChannelCount = 2 });

            Assert.True(store.TryLoad<SettingsModel>(out SettingsModel settings));
            Assert.NotNull(settings);
            Assert.Equal("Second", settings.DisplayName);
            Assert.Equal(2, settings.ChannelCount);

            string json = File.ReadAllText(dir.NewStorePath());
            Assert.Contains("Second", json);
            Assert.DoesNotContain("First", json);
        }

        /// <summary>
        /// Delete removes an existing settings file.
        /// </summary>
        [Fact]
        public void Delete_ExistingFile_RemovesIt()
        {
            using var dir = new TempDir();
            var store = new JsonSettingsStore(dir.NewStorePath());

            store.Save(new SettingsModel { DisplayName = "Probe" });
            Assert.True(File.Exists(dir.NewStorePath()));

            store.Delete();

            Assert.False(File.Exists(dir.NewStorePath()));
            Assert.False(store.Exists);
        }

        /// <summary>
        /// Delete on a store that has no file is a silent no-op.
        /// </summary>
        [Fact]
        public void Delete_MissingFile_IsNoOp()
        {
            using var dir = new TempDir();
            var store = new JsonSettingsStore(dir.NewStorePath());

            store.Delete();

            Assert.False(store.Exists);
        }

        /// <summary>
        /// Saving a null payload is rejected with ArgumentNullException.
        /// </summary>
        [Fact]
        public void Save_NullSettings_ThrowsArgumentNullException()
        {
            using var dir = new TempDir();
            var store = new JsonSettingsStore(dir.NewStorePath());

            Assert.Throws<ArgumentNullException>(() => store.Save<SettingsModel>(null));
        }

        /// <summary>
        /// When a parent path component is occupied by a file, Save cannot
        /// create the directory chain and the I/O failure propagates as
        /// IOException or UnauthorizedAccessException.
        /// </summary>
        [Fact]
        public void Save_ParentPathCollidesWithFile_Throws()
        {
            using var dir = new TempDir();
            string collidingParent = Path.Combine(dir.Root, "blocked");
            File.WriteAllText(collidingParent, "i am a file, not a directory");
            var store = new JsonSettingsStore(Path.Combine(collidingParent, "UserSettings.json"));

            Exception ex = Record.Exception(() => store.Save(new SettingsModel { DisplayName = "Probe" }));

            Assert.NotNull(ex);
            Assert.True(ex is IOException || ex is UnauthorizedAccessException, $"Expected IOException or UnauthorizedAccessException, got {ex.GetType().Name}");
        }
    }
}
