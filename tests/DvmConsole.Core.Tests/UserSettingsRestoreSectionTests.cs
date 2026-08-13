// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using dvmconsole;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DvmConsole.Core.Tests
{
    /// <summary>
    /// RED contract for the Core-owned Gate 3.4 restore section.
    /// The JSON shape is stable and contains no platform/runtime types.
    /// </summary>
    public sealed class UserSettingsRestoreSectionTests
    {
        [Fact]
        public void Type_IsPublicSealedWithStableRestoreProperties()
        {
            var type = typeof(UserSettingsRestoreSection);

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
                    nameof(UserSettingsRestoreSection.PrimaryResourceKey),
                    nameof(UserSettingsRestoreSection.SelectableEncryptionStates),
                    nameof(UserSettingsRestoreSection.SelectedChannels),
                    nameof(UserSettingsRestoreSection.SelectedWebStreams),
                },
                properties.Select(property => property.Name));
            Assert.Equal(typeof(string), properties[0].PropertyType);
            Assert.Equal(typeof(Dictionary<string, bool>), properties[1].PropertyType);
            Assert.Equal(typeof(List<string>), properties[2].PropertyType);
            Assert.Equal(typeof(List<string>), properties[3].PropertyType);
            Assert.All(properties, property => Assert.True(property.SetMethod!.IsPublic));
        }

        [Fact]
        public void Defaults_AreEmptyAndDoNotSelectAResource()
        {
            var section = new UserSettingsRestoreSection();

            Assert.Null(section.PrimaryResourceKey);
            Assert.Empty(section.SelectedChannels);
            Assert.Empty(section.SelectedWebStreams);
            Assert.Empty(section.SelectableEncryptionStates);
        }

        [Fact]
        public void SerializationAndRoundTrip_KeepPascalCaseRestoreValues()
        {
            var section = new UserSettingsRestoreSection
            {
                SelectedChannels = new List<string> { "System 1|31001", "System 1|31002" },
                PrimaryResourceKey = "System 1|31001",
                SelectableEncryptionStates = new Dictionary<string, bool>
                {
                    ["System 1|31001"] = true,
                    ["System 1|31002"] = false,
                },
            };

            string json = JsonConvert.SerializeObject(section, Formatting.Indented);
            var objectValue = JObject.Parse(json);

            Assert.Equal(4, objectValue.Properties().Count());
            Assert.Equal(2, objectValue[nameof(UserSettingsRestoreSection.SelectedChannels)]!.Count());
            Assert.Equal(
                "System 1|31001",
                (string)objectValue[nameof(UserSettingsRestoreSection.PrimaryResourceKey)]!);
            Assert.True((bool)objectValue[nameof(UserSettingsRestoreSection.SelectableEncryptionStates)]!["System 1|31001"]!);
            Assert.Null(objectValue["$type"]);
            Assert.DoesNotContain("selectedChannels", json);

            var loaded = JsonConvert.DeserializeObject<UserSettingsRestoreSection>(json);

            Assert.NotNull(loaded);
            Assert.Equal(section.SelectedChannels, loaded!.SelectedChannels);
            Assert.Equal(section.SelectedWebStreams, loaded.SelectedWebStreams);
            Assert.Equal(section.PrimaryResourceKey, loaded.PrimaryResourceKey);
            Assert.Equal(section.SelectableEncryptionStates, loaded.SelectableEncryptionStates);
        }

        [Fact]
        public void PartialJson_UsesDefaultsForMissingCollectionsAndPrimary()
        {
            var loaded = JsonConvert.DeserializeObject<UserSettingsRestoreSection>(
                "{\"SelectedChannels\":[\"System 1|31001\"]}");

            Assert.NotNull(loaded);
            Assert.Equal(new[] { "System 1|31001" }, loaded!.SelectedChannels);
            Assert.Null(loaded.PrimaryResourceKey);
            Assert.Empty(loaded.SelectableEncryptionStates);
        }
    }
}
