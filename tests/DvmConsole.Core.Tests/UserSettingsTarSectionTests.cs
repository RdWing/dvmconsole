// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using dvmconsole;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DvmConsole.Core.Tests
{
    /// <summary>
    /// RED contract gate for the Core-owned TAR settings section. The DTO
    /// preserves the WPF top-level JSON names while keeping TAR persistence
    /// independent of SettingsManager and Avalonia.
    /// </summary>
    public sealed class UserSettingsTarSectionTests
    {
        [Fact]
        public void Type_IsPublicSealedWithWpfCompatibleProperties()
        {
            var type = typeof(UserSettingsTarSection);

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
                    nameof(UserSettingsTarSection.TarChannelConfigs),
                    nameof(UserSettingsTarSection.TarRecordingsRootPath),
                },
                properties.Select(property => property.Name));
            Assert.Equal(
                typeof(Dictionary<string, TarChannelConfig>),
                properties[0].PropertyType);
            Assert.Equal(typeof(string), properties[1].PropertyType);
            Assert.All(properties, property => Assert.True(property.SetMethod!.IsPublic));
        }

        [Fact]
        public void Defaults_MatchWpfTarSettings_AndCollectionsAreFresh()
        {
            var first = new UserSettingsTarSection();
            var second = new UserSettingsTarSection();

            Assert.EndsWith(
                Path.Combine("DVMConsole", "TAR"),
                first.TarRecordingsRootPath,
                StringComparison.Ordinal);
            Assert.Empty(first.TarChannelConfigs);
            Assert.NotSame(first.TarChannelConfigs, second.TarChannelConfigs);

            first.TarChannelConfigs["sys|1"] = new TarChannelConfig { Enabled = true };
            Assert.Empty(second.TarChannelConfigs);
        }

        [Fact]
        public void SerializationAndRoundTrip_KeepPascalCaseValuesAndNoPlatformMetadata()
        {
            var section = new UserSettingsTarSection
            {
                TarRecordingsRootPath = "/tmp/recordings",
                TarChannelConfigs = new Dictionary<string, TarChannelConfig>
                {
                    ["sys|42"] = new TarChannelConfig
                    {
                        Enabled = true,
                        RetentionDays = 21,
                        IgnoredSubscriberIds = new List<uint> { 2, 7 },
                    },
                },
            };

            string json = JsonConvert.SerializeObject(section, Formatting.Indented);
            var objectValue = JObject.Parse(json);

            Assert.Equal(2, objectValue.Properties().Count());
            Assert.Equal("/tmp/recordings", (string)objectValue[nameof(UserSettingsTarSection.TarRecordingsRootPath)]);
            Assert.NotNull(objectValue[nameof(UserSettingsTarSection.TarChannelConfigs)]["sys|42"]);
            Assert.Null(objectValue["$type"]);
            Assert.DoesNotContain("tarRecordingsRootPath", json);

            var loaded = JsonConvert.DeserializeObject<UserSettingsTarSection>(json);

            Assert.NotNull(loaded);
            Assert.Equal("/tmp/recordings", loaded!.TarRecordingsRootPath);
            var config = Assert.Single(loaded.TarChannelConfigs);
            Assert.Equal("sys|42", config.Key);
            Assert.True(config.Value.Enabled);
            Assert.Equal(21, config.Value.RetentionDays);
            Assert.Equal(new uint[] { 2, 7 }, config.Value.IgnoredSubscriberIds);
        }
    }
}
