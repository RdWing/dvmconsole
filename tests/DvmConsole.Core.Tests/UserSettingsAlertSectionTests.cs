// SPDX-License-Identifier: AGPL-3.0-only
using System.Reflection;
using dvmconsole;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DvmConsole.Core.Tests
{
    /// <summary>
    /// RED contract gate for the Core-owned alert and tone-settings DTOs.
    /// Property names and nested JSON shapes must remain compatible with the
    /// WPF SettingsManager alert/preset schema.
    /// </summary>
    public sealed class UserSettingsAlertSectionTests
    {
        [Fact]
        public void Section_IsPublicSealedWithExactWpfCompatibleProperties()
        {
            var type = typeof(UserSettingsAlertSection);
            Assert.Equal("dvmconsole", type.Namespace);
            Assert.True(type.IsPublic);
            Assert.True(type.IsSealed);

            var properties = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(
                new[]
                {
                    nameof(UserSettingsAlertSection.AlertToneFilePaths),
                    nameof(UserSettingsAlertSection.AlertTonePositions),
                    nameof(UserSettingsAlertSection.AlertToneTabs),
                    nameof(UserSettingsAlertSection.AlertTones),
                    nameof(UserSettingsAlertSection.DtmfPresets),
                    nameof(UserSettingsAlertSection.TonePresets)
                },
                properties.Select(property => property.Name));
            Assert.Equal(typeof(List<string>), properties[0].PropertyType);
            Assert.Equal(typeof(Dictionary<string, UserSettingsLayoutPosition>), properties[1].PropertyType);
            Assert.Equal(typeof(Dictionary<string, string>), properties[2].PropertyType);
            Assert.Equal(typeof(List<UserSettingsAlertToneConfig>), properties[3].PropertyType);
            Assert.Equal(typeof(List<UserSettingsDtmfPresetConfig>), properties[4].PropertyType);
            Assert.Equal(typeof(List<UserSettingsTonePresetConfig>), properties[5].PropertyType);
            Assert.All(properties, property => Assert.True(property.SetMethod!.IsPublic));
        }

        [Fact]
        public void Defaults_MatchWpfAlertAndPresetDefaults()
        {
            var section = new UserSettingsAlertSection();

            Assert.Empty(section.AlertToneFilePaths);
            Assert.Empty(section.AlertTonePositions);
            Assert.Empty(section.AlertToneTabs);
            Assert.Empty(section.AlertTones);
            Assert.Empty(section.TonePresets);
            Assert.Empty(section.DtmfPresets);

            var alert = new UserSettingsAlertToneConfig();
            Assert.False(string.IsNullOrWhiteSpace(alert.Id));
            Assert.Equal(string.Empty, alert.DisplayName);
            Assert.Equal(string.Empty, alert.FilePath);
            Assert.Equal(string.Empty, alert.TabName);
            Assert.Equal(20d, alert.Position.X);
            Assert.Equal(20d, alert.Position.Y);

            var toneStep = new UserSettingsTonePresetStep();
            Assert.Equal("tone", toneStep.Kind);
            Assert.Equal(1000d, toneStep.FrequencyHz);
            Assert.Equal(1d, toneStep.DurationSeconds);

            var dtmfStep = new UserSettingsDtmfPresetStep();
            Assert.Equal("digit", dtmfStep.Kind);
            Assert.Equal("1", dtmfStep.Digit);
            Assert.Equal(0.25d, dtmfStep.DurationSeconds);
        }

        [Fact]
        public void NestedDtos_ExposeWpfCompatibleMutableProperties()
        {
            Assert.Equal(
                new[] { "DisplayName", "FilePath", "Id", "Position", "TabName" },
                typeof(UserSettingsAlertToneConfig)
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .OrderBy(property => property.Name)
                    .Select(property => property.Name));
            Assert.Equal(
                new[] { "DisplayName", "Id", "Steps", "TargetResourceKey" },
                typeof(UserSettingsTonePresetConfig)
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .OrderBy(property => property.Name)
                    .Select(property => property.Name));
            Assert.Equal(
                new[] { "DisplayName", "Id", "Steps", "TargetResourceKey" },
                typeof(UserSettingsDtmfPresetConfig)
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .OrderBy(property => property.Name)
                    .Select(property => property.Name));
        }

        [Fact]
        public void SerializationAndRoundTrip_PreserveLegacyAndNestedPresetShape()
        {
            var section = new UserSettingsAlertSection
            {
                AlertToneFilePaths = new List<string> { "alert.wav" },
                AlertToneTabs = new Dictionary<string, string> { ["alert.wav"] = "Operations" },
                AlertTonePositions = new Dictionary<string, UserSettingsLayoutPosition>
                {
                    ["alert.wav"] = new UserSettingsLayoutPosition { X = 3, Y = 4 }
                },
                AlertTones = new List<UserSettingsAlertToneConfig>
                {
                    new UserSettingsAlertToneConfig
                    {
                        Id = "alert-id",
                        DisplayName = "Alert",
                        FilePath = "alert.wav",
                        TabName = "Operations",
                        Position = new UserSettingsLayoutPosition { X = 5, Y = 6 }
                    }
                },
                TonePresets = new List<UserSettingsTonePresetConfig>
                {
                    new UserSettingsTonePresetConfig
                    {
                        Id = "tone-id",
                        DisplayName = "Call tone",
                        TargetResourceKey = "System|100",
                        Steps = new List<UserSettingsTonePresetStep>
                        {
                            new UserSettingsTonePresetStep { Kind = "hold", FrequencyHz = 0, DurationSeconds = 0.5 }
                        }
                    }
                },
                DtmfPresets = new List<UserSettingsDtmfPresetConfig>
                {
                    new UserSettingsDtmfPresetConfig
                    {
                        Id = "dtmf-id",
                        DisplayName = "Dispatch",
                        TargetResourceKey = "System|100",
                        Steps = new List<UserSettingsDtmfPresetStep>
                        {
                            new UserSettingsDtmfPresetStep { Kind = "digit", Digit = "5", DurationSeconds = 0.75 }
                        }
                    }
                }
            };

            string json = JsonConvert.SerializeObject(section, Formatting.Indented);
            var objectValue = JObject.Parse(json);

            Assert.Equal(6, objectValue.Properties().Count());
            Assert.NotNull(objectValue[nameof(UserSettingsAlertSection.AlertToneFilePaths)]);
            Assert.NotNull(objectValue[nameof(UserSettingsAlertSection.AlertTonePositions)]);
            Assert.NotNull(objectValue[nameof(UserSettingsAlertSection.AlertToneTabs)]);
            Assert.NotNull(objectValue[nameof(UserSettingsAlertSection.AlertTones)]);
            Assert.NotNull(objectValue[nameof(UserSettingsAlertSection.TonePresets)]);
            Assert.NotNull(objectValue[nameof(UserSettingsAlertSection.DtmfPresets)]);
            Assert.Null(objectValue["$type"]);
            Assert.DoesNotContain("alertTones", json);

            var loaded = JsonConvert.DeserializeObject<UserSettingsAlertSection>(json);

            Assert.NotNull(loaded);
            Assert.Equal("Operations", loaded!.AlertToneTabs["alert.wav"]);
            Assert.Equal(6d, loaded.AlertTones[0].Position.Y);
            Assert.Equal("hold", loaded.TonePresets[0].Steps[0].Kind);
            Assert.Equal(0.5d, loaded.TonePresets[0].Steps[0].DurationSeconds);
            Assert.Equal("5", loaded.DtmfPresets[0].Steps[0].Digit);
            Assert.Equal(0.75d, loaded.DtmfPresets[0].Steps[0].DurationSeconds);
        }
    }
}
