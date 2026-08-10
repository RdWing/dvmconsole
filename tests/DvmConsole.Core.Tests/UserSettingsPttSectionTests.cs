// SPDX-License-Identifier: AGPL-3.0-only
using System.Reflection;
using dvmconsole;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DvmConsole.Core.Tests
{
    /// <summary>
    /// RED contract gate for the Core-owned PTT settings section.
    /// The persisted key code remains WPF-compatible while avoiding a
    /// Platform hotkey dependency in Core.
    /// </summary>
    public sealed class UserSettingsPttSectionTests
    {
        [Fact]
        public void Type_IsPublicSealedWithWpfCompatibleProperties()
        {
            var type = typeof(UserSettingsPttSection);

            Assert.Equal("dvmconsole", type.Namespace);
            Assert.True(type.IsPublic);
            Assert.True(type.IsSealed);

            var properties = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .OrderBy(property => property.Name)
                .ToArray();

            Assert.Equal(
                new[]
                {
                    nameof(UserSettingsPttSection.GlobalPTTKeysAllChannels),
                    nameof(UserSettingsPttSection.GlobalPTTShortcut),
                    nameof(UserSettingsPttSection.TogglePTTMode)
                },
                properties.Select(property => property.Name));
            Assert.Equal(typeof(bool), properties[0].PropertyType);
            Assert.Equal(typeof(int), properties[1].PropertyType);
            Assert.Equal(typeof(bool), properties[2].PropertyType);
            Assert.All(properties, property => Assert.True(property.SetMethod!.IsPublic));
        }

        [Fact]
        public void Defaults_MatchWpfPttSettings()
        {
            var section = new UserSettingsPttSection();

            Assert.False(section.TogglePTTMode);
            Assert.Equal(0, section.GlobalPTTShortcut);
            Assert.False(section.GlobalPTTKeysAllChannels);
        }

        [Fact]
        public void SerializationAndRoundTrip_KeepPascalCaseValuesAndNoPlatformMetadata()
        {
            var section = new UserSettingsPttSection
            {
                TogglePTTMode = true,
                GlobalPTTShortcut = 123,
                GlobalPTTKeysAllChannels = true
            };

            string json = JsonConvert.SerializeObject(section, Formatting.Indented);
            var objectValue = JObject.Parse(json);

            Assert.Equal(3, objectValue.Properties().Count());
            Assert.True((bool)objectValue[nameof(UserSettingsPttSection.TogglePTTMode)]);
            Assert.Equal(123, (int)objectValue[nameof(UserSettingsPttSection.GlobalPTTShortcut)]);
            Assert.True((bool)objectValue[nameof(UserSettingsPttSection.GlobalPTTKeysAllChannels)]);
            Assert.Null(objectValue["$type"]);
            Assert.DoesNotContain("globalPTTShortcut", json);

            var loaded = JsonConvert.DeserializeObject<UserSettingsPttSection>(json);

            Assert.NotNull(loaded);
            Assert.True(loaded!.TogglePTTMode);
            Assert.Equal(123, loaded.GlobalPTTShortcut);
            Assert.True(loaded.GlobalPTTKeysAllChannels);
        }
    }
}
