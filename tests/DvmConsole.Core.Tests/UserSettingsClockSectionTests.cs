// SPDX-License-Identifier: AGPL-3.0-only
using System;
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
    /// RED contract for the Core-owned toolbar-clock settings section.
    /// Storage keeps the WPF list and slot shapes; normalization is an explicit
    /// boundary operation for load/save and does not run in property setters.
    /// </summary>
    public sealed class UserSettingsClockSectionTests
    {
        [Fact]
        public void Section_IsPublicSealedWithExactWpfClockProperties()
        {
            var type = typeof(UserSettingsClockSection);
            var properties = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal("dvmconsole", type.Namespace);
            Assert.True(type.IsPublic);
            Assert.True(type.IsSealed);
            Assert.Equal(
                new[]
                {
                    nameof(UserSettingsClockSection.ClockShowSeconds),
                    nameof(UserSettingsClockSection.ClockUse24HourTime),
                    nameof(UserSettingsClockSection.ToolbarClockConfigSlots),
                    nameof(UserSettingsClockSection.ToolbarClockConfigs)
                },
                properties.Select(property => property.Name));
            Assert.Equal(typeof(bool), properties[0].PropertyType);
            Assert.Equal(typeof(bool), properties[1].PropertyType);
            Assert.Equal(typeof(Dictionary<string, ToolbarClockConfig>), properties[2].PropertyType);
            Assert.Equal(typeof(List<ToolbarClockConfig>), properties[3].PropertyType);
            Assert.All(properties, property => Assert.True(property.SetMethod!.IsPublic));
        }

        [Fact]
        public void ToolbarClockConfig_IsPublicSealedWithExactMutableProperties()
        {
            var type = typeof(ToolbarClockConfig);
            var properties = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal("dvmconsole", type.Namespace);
            Assert.True(type.IsPublic);
            Assert.True(type.IsSealed);
            Assert.Equal(
                new[] { nameof(ToolbarClockConfig.ColorHex), nameof(ToolbarClockConfig.Enabled), nameof(ToolbarClockConfig.UtcOffsetHours) },
                properties.Select(property => property.Name));
            Assert.Equal(typeof(string), properties[0].PropertyType);
            Assert.Equal(typeof(bool), properties[1].PropertyType);
            Assert.Equal(typeof(int), properties[2].PropertyType);
            Assert.All(properties, property => Assert.True(property.SetMethod!.IsPublic));
        }

        [Fact]
        public void Defaults_MatchWpfEightSlotClockSettings_AndCollectionsAreFresh()
        {
            var first = new UserSettingsClockSection();
            var second = new UserSettingsClockSection();

            Assert.True(first.ClockUse24HourTime);
            Assert.True(first.ClockShowSeconds);
            Assert.Equal(8, first.ToolbarClockConfigs.Count);
            Assert.Equal(8, first.ToolbarClockConfigSlots.Count);
            Assert.Equal(Enumerable.Range(1, 8).Select(slot => slot.ToString()), first.ToolbarClockConfigSlots.Keys.OrderBy(key => key));
            Assert.All(first.ToolbarClockConfigs, AssertDefaultClock);
            Assert.All(first.ToolbarClockConfigSlots.Values, AssertDefaultClock);
            Assert.NotSame(first.ToolbarClockConfigs, second.ToolbarClockConfigs);
            Assert.NotSame(first.ToolbarClockConfigSlots, second.ToolbarClockConfigSlots);
            Assert.NotSame(first.ToolbarClockConfigs[0], second.ToolbarClockConfigs[0]);
        }

        [Fact]
        public void SerializationAndRoundTrip_PreserveWpfClockJsonShape()
        {
            var section = new UserSettingsClockSection
            {
                ClockUse24HourTime = false,
                ClockShowSeconds = false,
                ToolbarClockConfigs = new List<ToolbarClockConfig>
                {
                    new ToolbarClockConfig { Enabled = true, UtcOffsetHours = -5, ColorHex = "#0D47A1" }
                },
                ToolbarClockConfigSlots = new Dictionary<string, ToolbarClockConfig>
                {
                    ["1"] = new ToolbarClockConfig { Enabled = true, UtcOffsetHours = -5, ColorHex = "#0D47A1" }
                }
            };

            string json = JsonConvert.SerializeObject(section, Formatting.Indented);
            var objectValue = JObject.Parse(json);

            Assert.Equal(4, objectValue.Properties().Count());
            Assert.NotNull(objectValue[nameof(UserSettingsClockSection.ToolbarClockConfigs)]);
            Assert.NotNull(objectValue[nameof(UserSettingsClockSection.ToolbarClockConfigSlots)]!["1"]);
            Assert.NotNull(objectValue[nameof(UserSettingsClockSection.ClockUse24HourTime)]);
            Assert.NotNull(objectValue[nameof(UserSettingsClockSection.ClockShowSeconds)]);
            Assert.NotNull(objectValue[nameof(UserSettingsClockSection.ToolbarClockConfigs)]![0]![nameof(ToolbarClockConfig.Enabled)]);
            Assert.Null(objectValue["$type"]);
            Assert.DoesNotContain("toolbarClockConfigs", json);

            var loaded = JsonConvert.DeserializeObject<UserSettingsClockSection>(json);

            Assert.NotNull(loaded);
            Assert.False(loaded!.ClockUse24HourTime);
            Assert.False(loaded.ClockShowSeconds);
            Assert.True(loaded.ToolbarClockConfigs[0].Enabled);
            Assert.Equal(-5, loaded.ToolbarClockConfigs[0].UtcOffsetHours);
            Assert.Equal("#0D47A1", loaded.ToolbarClockConfigSlots["1"].ColorHex);
        }

        [Fact]
        public void Normalize_TruncatesPadsAndNormalizesListValues()
        {
            var source = new UserSettingsClockSection
            {
                ToolbarClockConfigs = new List<ToolbarClockConfig>
                {
                    new ToolbarClockConfig { Enabled = true, UtcOffsetHours = -99, ColorHex = "  #0d47a1  " },
                    null!,
                    new ToolbarClockConfig { Enabled = true, UtcOffsetHours = 99, ColorHex = "not-a-color" },
                    new ToolbarClockConfig(),
                    new ToolbarClockConfig(),
                    new ToolbarClockConfig(),
                    new ToolbarClockConfig(),
                    new ToolbarClockConfig(),
                    new ToolbarClockConfig { Enabled = true, UtcOffsetHours = 4, ColorHex = "#ffffff" }
                },
                ToolbarClockConfigSlots = null!
            };

            var normalized = UserSettingsClockSection.Normalize(source);

            Assert.Equal(8, normalized.ToolbarClockConfigs.Count);
            Assert.Equal(8, normalized.ToolbarClockConfigSlots.Count);
            Assert.True(normalized.ToolbarClockConfigs[0].Enabled);
            Assert.Equal(-12, normalized.ToolbarClockConfigs[0].UtcOffsetHours);
            Assert.Equal("#0D47A1", normalized.ToolbarClockConfigs[0].ColorHex);
            Assert.False(normalized.ToolbarClockConfigs[1].Enabled);
            Assert.Equal(0, normalized.ToolbarClockConfigs[1].UtcOffsetHours);
            Assert.Equal("#3A3A3A", normalized.ToolbarClockConfigs[1].ColorHex);
            Assert.Equal(14, normalized.ToolbarClockConfigs[2].UtcOffsetHours);
            Assert.Equal("#3A3A3A", normalized.ToolbarClockConfigs[2].ColorHex);
            Assert.False(normalized.ToolbarClockConfigs[7].Enabled);
            Assert.Equal("#3A3A3A", normalized.ToolbarClockConfigs[7].ColorHex);
        }

        [Fact]
        public void Normalize_SlotsTakePrecedenceAndUseCaseInsensitiveKeys()
        {
            var source = new UserSettingsClockSection
            {
                ToolbarClockConfigs = new List<ToolbarClockConfig>
                {
                    new ToolbarClockConfig { Enabled = false, UtcOffsetHours = 2, ColorHex = "#111111" },
                    new ToolbarClockConfig { Enabled = true, UtcOffsetHours = 3, ColorHex = "#222222" }
                },
                ToolbarClockConfigSlots = new Dictionary<string, ToolbarClockConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["1"] = new ToolbarClockConfig { Enabled = true, UtcOffsetHours = 7, ColorHex = "#abcdef" },
                    ["3"] = new ToolbarClockConfig { Enabled = true, UtcOffsetHours = 99, ColorHex = "#123456" },
                    ["8"] = new ToolbarClockConfig { Enabled = true, UtcOffsetHours = -99, ColorHex = "#654321" }
                }
            };

            var normalized = UserSettingsClockSection.Normalize(source);

            Assert.True(normalized.ToolbarClockConfigs[0].Enabled);
            Assert.Equal(7, normalized.ToolbarClockConfigs[0].UtcOffsetHours);
            Assert.Equal("#ABCDEF", normalized.ToolbarClockConfigs[0].ColorHex);
            Assert.True(normalized.ToolbarClockConfigs[1].Enabled);
            Assert.Equal(3, normalized.ToolbarClockConfigs[1].UtcOffsetHours);
            Assert.Equal("#222222", normalized.ToolbarClockConfigs[1].ColorHex);
            Assert.Equal(14, normalized.ToolbarClockConfigs[2].UtcOffsetHours);
            Assert.Equal(-12, normalized.ToolbarClockConfigs[7].UtcOffsetHours);
            Assert.Equal(8, normalized.ToolbarClockConfigSlots.Count);
            Assert.Equal(normalized.ToolbarClockConfigs[2].UtcOffsetHours, normalized.ToolbarClockConfigSlots["3"].UtcOffsetHours);
        }

        [Fact]
        public void NormalizeForSave_UsesListAsCanonicalAndRegeneratesSlots()
        {
            var source = new UserSettingsClockSection
            {
                ToolbarClockConfigs = new List<ToolbarClockConfig>
                {
                    new ToolbarClockConfig { Enabled = true, UtcOffsetHours = -5, ColorHex = "#abcdef" }
                }
            };

            var normalized = UserSettingsClockSection.NormalizeForSave(source);

            Assert.True(normalized.ToolbarClockConfigs[0].Enabled);
            Assert.Equal(-5, normalized.ToolbarClockConfigs[0].UtcOffsetHours);
            Assert.Equal("#ABCDEF", normalized.ToolbarClockConfigs[0].ColorHex);
            Assert.True(normalized.ToolbarClockConfigSlots["1"].Enabled);
            Assert.Equal(-5, normalized.ToolbarClockConfigSlots["1"].UtcOffsetHours);
            Assert.Equal("#ABCDEF", normalized.ToolbarClockConfigSlots["1"].ColorHex);
            Assert.Equal(normalized.ToolbarClockConfigs[0].ColorHex, normalized.ToolbarClockConfigSlots["1"].ColorHex);
        }

        private static void AssertDefaultClock(ToolbarClockConfig config)
        {
            Assert.False(config.Enabled);
            Assert.Equal(0, config.UtcOffsetHours);
            Assert.Equal(UserSettingsClockSection.DEFAULT_TOOLBAR_CLOCK_COLOR, config.ColorHex);
        }
    }
}
