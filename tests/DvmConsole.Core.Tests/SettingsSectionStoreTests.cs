// SPDX-License-Identifier: AGPL-3.0-only
using System.Text;
using dvmconsole;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DvmConsole.Core.Tests
{
    /// <summary>
    /// RED contract gate for the merge-preserving Core settings-section store.
    /// It must update only the section's owned properties and never destroy
    /// unrelated WPF settings.
    /// </summary>
    public sealed class SettingsSectionStoreTests
    {
        private sealed class TempDir : IDisposable
        {
            public string Root { get; } = Path.Combine(
                Path.GetTempPath(),
                "dvmconsole-settings-section-" + Guid.NewGuid().ToString("N"));

            public TempDir()
            {
                Directory.CreateDirectory(Root);
            }

            public string StorePath(string fileName = "UserSettings.json")
                => Path.Combine(Root, fileName);

            public void Dispose()
            {
                try
                {
                    if (Directory.Exists(Root))
                    {
                        Directory.Delete(Root, recursive: true);
                    }
                }
                catch (IOException)
                {
                    // Best-effort cleanup; never mask the test result.
                }
            }
        }

        [Fact]
        public void Type_IsPublicSealedAndBoundToOneImmutablePath()
        {
            var type = typeof(SettingsSectionStore);

            Assert.Equal("dvmconsole", type.Namespace);
            Assert.True(type.IsPublic);
            Assert.True(type.IsSealed);

            var constructor = type.GetConstructor(new[] { typeof(string) });
            Assert.NotNull(constructor);

            var publicInstanceMethods = type
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Where(method => method.DeclaringType == type && !method.IsSpecialName)
                .ToArray();
            Assert.Equal(2, publicInstanceMethods.Length);
            Assert.Contains(publicInstanceMethods, method => method.Name == "TryLoadSection");
            Assert.Contains(publicInstanceMethods, method => method.Name == "SaveSection");
            Assert.Empty(type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance));
        }

        [Fact]
        public void TryLoadSection_MissingFile_ReturnsFalseWithDefaults()
        {
            using var dir = new TempDir();
            var store = new SettingsSectionStore(dir.StorePath());

            Assert.False(store.TryLoadSection<UserSettingsAudioSection>(out var section));

            Assert.NotNull(section);
            Assert.Equal("windows-default", section.AudioInputDeviceKey);
            Assert.Equal("windows-default", section.MasterOutputDeviceKey);
            Assert.False(section.AudioInputAgcEnabled);
        }

        [Fact]
        public void TryLoadSection_EmptyOrMalformedFile_ReturnsFalseWithDefaultsWithoutThrowing()
        {
            using var dir = new TempDir();
            var path = dir.StorePath();
            var store = new SettingsSectionStore(path);

            File.WriteAllText(path, string.Empty);
            Assert.False(store.TryLoadSection<UserSettingsAudioSection>(out var empty));
            Assert.Equal("windows-default", empty.AudioInputDeviceKey);
            Assert.False(empty.AudioInputAgcEnabled);

            File.WriteAllText(path, "{ not json !!!");
            Assert.False(store.TryLoadSection<UserSettingsAudioSection>(out var malformed));
            Assert.Equal("windows-default", malformed.MasterOutputDeviceKey);
            Assert.False(malformed.AudioInputAgcEnabled);
        }

        [Fact]
        public void TryLoadSection_PartialJson_HonorsPresentKeysAndUsesDtoDefaultsForMissingKeys()
        {
            using var dir = new TempDir();
            var path = dir.StorePath();
            File.WriteAllText(
                path,
                "{\"AudioInputDeviceKey\":\"only-input\",\"Unrelated\":42}");
            var store = new SettingsSectionStore(path);

            Assert.True(store.TryLoadSection<UserSettingsAudioSection>(out var section));

            Assert.Equal("only-input", section.AudioInputDeviceKey);
            Assert.Equal("windows-default", section.MasterOutputDeviceKey);
            Assert.False(section.AudioInputAgcEnabled);
        }

        [Fact]
        public void SaveSection_MissingFile_CreatesParentDirectoriesAndOnlyOwnedKeys()
        {
            using var dir = new TempDir();
            var path = dir.StorePath(Path.Combine("nested", "deep", "UserSettings.json"));
            var store = new SettingsSectionStore(path);
            var section = new UserSettingsAudioSection
            {
                AudioInputDeviceKey = "input-key",
                MasterOutputDeviceKey = "output-key",
                AudioInputAgcEnabled = true
            };

            store.SaveSection(section);

            Assert.True(File.Exists(path));
            var saved = JObject.Parse(File.ReadAllText(path));
            Assert.Equal(7, saved.Properties().Count());
            Assert.Equal("input-key", (string)saved[nameof(UserSettingsAudioSection.AudioInputDeviceKey)]);
            Assert.Equal("output-key", (string)saved[nameof(UserSettingsAudioSection.MasterOutputDeviceKey)]);
            Assert.True((bool)saved[nameof(UserSettingsAudioSection.AudioInputAgcEnabled)]);
            Assert.Empty(saved[nameof(UserSettingsAudioSection.ChannelOutputDevices)]!);
            Assert.Empty(saved[nameof(UserSettingsAudioSection.ChannelOutputDeviceKeys)]!);
            Assert.Empty(saved[nameof(UserSettingsAudioSection.ChannelVolumes)]!);
            Assert.Empty(saved[nameof(UserSettingsAudioSection.WebStreamVolumes)]!);
        }

        [Fact]
        public void SaveSection_UpdatesOwnedKeysAndPreservesUnknownScalarsNestedObjectsAndArrays()
        {
            using var dir = new TempDir();
            var path = dir.StorePath();
            File.WriteAllText(
                path,
                """
                {
                  "AudioInputDeviceKey": "old-input",
                  "MasterOutputDeviceKey": "old-output",
                  "AudioInputAgcEnabled": false,
                  "UnrelatedString": "keep verbatim",
                  "UnrelatedNumber": 17,
                  "UnrelatedObject": { "Nested": true, "Name": "untouched" },
                  "UnrelatedArray": [1, { "Value": "untouched" }, [true, false]]
                }
                """);
            var before = JObject.Parse(File.ReadAllText(path));
            var store = new SettingsSectionStore(path);

            store.SaveSection(new UserSettingsAudioSection
            {
                AudioInputDeviceKey = "new-input",
                MasterOutputDeviceKey = string.Empty,
                AudioInputAgcEnabled = true
            });

            var after = JObject.Parse(File.ReadAllText(path));
            Assert.Equal("new-input", (string)after[nameof(UserSettingsAudioSection.AudioInputDeviceKey)]);
            Assert.Equal(string.Empty, (string)after[nameof(UserSettingsAudioSection.MasterOutputDeviceKey)]);
            Assert.True((bool)after[nameof(UserSettingsAudioSection.AudioInputAgcEnabled)]);
            Assert.Equal("keep verbatim", (string)after["UnrelatedString"]);
            Assert.Equal(17, (int)after["UnrelatedNumber"]);
            Assert.True(JToken.DeepEquals(before["UnrelatedObject"], after["UnrelatedObject"]));
            Assert.True(JToken.DeepEquals(before["UnrelatedArray"], after["UnrelatedArray"]));
        }

        [Fact]
        public void SaveSection_MalformedExistingFile_ThrowsAndDoesNotOverwriteIt()
        {
            using var dir = new TempDir();
            var path = dir.StorePath();
            const string malformed = "{ not valid json";
            File.WriteAllText(path, malformed);
            var store = new SettingsSectionStore(path);

            Assert.ThrowsAny<Exception>(() => store.SaveSection(new UserSettingsAudioSection
            {
                AudioInputDeviceKey = "must-not-write"
            }));
            Assert.Equal(malformed, File.ReadAllText(path));
        }

        [Fact]
        public void SaveThenLoad_RoundTripsTheAudioSection()
        {
            using var dir = new TempDir();
            var store = new SettingsSectionStore(dir.StorePath());
            var expected = new UserSettingsAudioSection
            {
                AudioInputDeviceKey = "round-trip-input",
                MasterOutputDeviceKey = "round-trip-output",
                AudioInputAgcEnabled = true
            };

            store.SaveSection(expected);

            Assert.True(store.TryLoadSection<UserSettingsAudioSection>(out var actual));
            Assert.Equal(expected.AudioInputDeviceKey, actual.AudioInputDeviceKey);
            Assert.Equal(expected.MasterOutputDeviceKey, actual.MasterOutputDeviceKey);
            Assert.Equal(expected.AudioInputAgcEnabled, actual.AudioInputAgcEnabled);
        }

        [Fact]
        public void SavedJson_IsIndentedUtf8WithoutBom()
        {
            using var dir = new TempDir();
            var path = dir.StorePath();
            var store = new SettingsSectionStore(path);

            store.SaveSection(new UserSettingsAudioSection
            {
                AudioInputDeviceKey = "Café-input"
            });

            byte[] bytes = File.ReadAllBytes(path);
            Assert.False(bytes.Length >= 3
                && bytes[0] == 0xEF
                && bytes[1] == 0xBB
                && bytes[2] == 0xBF);
            string json = Encoding.UTF8.GetString(bytes);
            Assert.Contains(Environment.NewLine, json);
            Assert.Contains("  \"AudioInputDeviceKey\"", json);
        }
    }
}
