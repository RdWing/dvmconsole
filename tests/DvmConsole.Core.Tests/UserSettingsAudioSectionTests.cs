// SPDX-License-Identifier: AGPL-3.0-only
using System.Reflection;
using dvmconsole;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DvmConsole.Core.Tests
{
    /// <summary>
    /// RED contract gate for the Core-owned audio-settings section DTO.
    /// The property names and defaults must remain byte-compatible with the
    /// existing WPF SettingsManager schema.
    /// </summary>
    public sealed class UserSettingsAudioSectionTests
    {
        [Fact]
        public void Type_IsPublicSealedCoreDto_WithExactlySevenMutablePascalCaseProperties()
        {
            var type = typeof(UserSettingsAudioSection);

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
                    nameof(UserSettingsAudioSection.AudioInputAgcEnabled),
                    nameof(UserSettingsAudioSection.AudioInputDeviceKey),
                    nameof(UserSettingsAudioSection.ChannelOutputDeviceKeys),
                    nameof(UserSettingsAudioSection.ChannelOutputDevices),
                    nameof(UserSettingsAudioSection.ChannelVolumes),
                    nameof(UserSettingsAudioSection.MasterOutputDeviceKey),
                    nameof(UserSettingsAudioSection.WebStreamVolumes)
                },
                properties.Select(property => property.Name));
            Assert.Equal(typeof(bool), properties[0].PropertyType);
            Assert.Equal(typeof(string), properties[1].PropertyType);
            Assert.Equal(typeof(Dictionary<string, string>), properties[2].PropertyType);
            Assert.Equal(typeof(Dictionary<string, int>), properties[3].PropertyType);
            Assert.Equal(typeof(Dictionary<string, double>), properties[4].PropertyType);
            Assert.Equal(typeof(string), properties[5].PropertyType);
            Assert.Equal(typeof(Dictionary<string, double>), properties[6].PropertyType);
            Assert.All(properties, property =>
            {
                Assert.NotNull(property.GetMethod);
                Assert.NotNull(property.SetMethod);
                Assert.True(property.SetMethod!.IsPublic);
            });
        }

        [Fact]
        public void Defaults_MatchWpfWindowsDefaultSemantics()
        {
            var section = new UserSettingsAudioSection();

            Assert.Equal("windows-default", section.AudioInputDeviceKey);
            Assert.Equal("windows-default", section.MasterOutputDeviceKey);
            Assert.False(section.AudioInputAgcEnabled);
        }

        [Fact]
        public void Serialization_EmitsExactlyTheSevenPascalCaseKeys_WithNoTypeMetadata()
        {
            var section = new UserSettingsAudioSection
            {
                AudioInputDeviceKey = "CoreAudio:input:USB",
                MasterOutputDeviceKey = string.Empty,
                AudioInputAgcEnabled = true
            };

            string json = JsonConvert.SerializeObject(section, Formatting.Indented);
            var objectValue = JObject.Parse(json);

            Assert.Equal(7, objectValue.Properties().Count());
            Assert.NotNull(objectValue[nameof(UserSettingsAudioSection.AudioInputDeviceKey)]);
            Assert.NotNull(objectValue[nameof(UserSettingsAudioSection.MasterOutputDeviceKey)]);
            Assert.NotNull(objectValue[nameof(UserSettingsAudioSection.AudioInputAgcEnabled)]);
            Assert.NotNull(objectValue[nameof(UserSettingsAudioSection.ChannelOutputDevices)]);
            Assert.NotNull(objectValue[nameof(UserSettingsAudioSection.ChannelOutputDeviceKeys)]);
            Assert.NotNull(objectValue[nameof(UserSettingsAudioSection.ChannelVolumes)]);
            Assert.NotNull(objectValue[nameof(UserSettingsAudioSection.WebStreamVolumes)]);
            Assert.Null(objectValue["$type"]);
            Assert.Contains(Environment.NewLine, json);
            Assert.DoesNotContain("audioInputDeviceKey", json);
            Assert.DoesNotContain("masterOutputDeviceKey", json);
            Assert.DoesNotContain("audioInputAgcEnabled", json);
        }

        [Fact]
        public void NonDefaultAndEmptyDeviceKeys_RoundTripVerbatimWithoutNormalization()
        {
            var section = new UserSettingsAudioSection
            {
                AudioInputDeviceKey = "MiXeD-Input-Key",
                MasterOutputDeviceKey = string.Empty,
                AudioInputAgcEnabled = true
            };

            string json = JsonConvert.SerializeObject(section);
            var loaded = JsonConvert.DeserializeObject<UserSettingsAudioSection>(json);

            Assert.NotNull(loaded);
            Assert.Equal("MiXeD-Input-Key", loaded!.AudioInputDeviceKey);
            Assert.Equal(string.Empty, loaded.MasterOutputDeviceKey);
            Assert.True(loaded.AudioInputAgcEnabled);
        }
    }
}
