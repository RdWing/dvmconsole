// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dvmconsole;
using DvmConsole.Avalonia.Persistence;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for the Avalonia TAR settings adapter: WPF-compatible
    /// normalization at the persistence boundary plus merge-preserving storage.
    /// </summary>
    public sealed class TarSettingsPersistenceTests
    {
        [Fact]
        public void Constructor_RejectsNullStore()
        {
            Assert.Throws<ArgumentNullException>(() => new TarSettingsPersistence(null!));
        }

        [Fact]
        public void TryLoad_MissingFile_ReturnsDefaultsWithoutThrowing()
        {
            string filePath = TemporarySettingsPath();
            try
            {
                var persistence = new TarSettingsPersistence(new SettingsSectionStore(filePath));

                Assert.False(persistence.TryLoad(out UserSettingsTarSection section));
                Assert.EndsWith(Path.Combine("DVMConsole", "TAR"), section.TarRecordingsRootPath, StringComparison.Ordinal);
                Assert.Empty(section.TarChannelConfigs);
            }
            finally
            {
                DeleteSettingsFile(filePath);
            }
        }

        [Fact]
        public void TryLoad_NormalizesRootKeysAndChannelConfigLikeWpf()
        {
            string filePath = TemporarySettingsPath();
            try
            {
                File.WriteAllText(
                    filePath,
                    "{\"KeepMe\":true,\"TarRecordingsRootPath\":\"  /tmp/tar  \",\"TarChannelConfigs\":{\" sys|42 \":{\"Enabled\":false,\"RetentionDays\":30,\"IgnoredSubscriberIds\":[7,0,7]}}}");
                var persistence = new TarSettingsPersistence(new SettingsSectionStore(filePath));

                Assert.True(persistence.TryLoad(out UserSettingsTarSection section));

                Assert.Equal("/tmp/tar", section.TarRecordingsRootPath);
                var config = Assert.Single(section.TarChannelConfigs);
                Assert.Equal("sys|42", config.Key);
                Assert.False(config.Value.Enabled);
                Assert.Equal(30, config.Value.RetentionDays);
                Assert.Equal(new uint[] { 7 }, config.Value.IgnoredSubscriberIds);
            }
            finally
            {
                DeleteSettingsFile(filePath);
            }
        }

        [Fact]
        public void TryLoad_LegacyEmpty30DayConfigDefaultsToSevenDays()
        {
            string filePath = TemporarySettingsPath();
            try
            {
                File.WriteAllText(
                    filePath,
                    "{\"TarChannelConfigs\":{\"sys|42\":{\"Enabled\":false,\"RetentionDays\":30,\"IgnoredSubscriberIds\":[]}}}");
                var persistence = new TarSettingsPersistence(new SettingsSectionStore(filePath));

                Assert.True(persistence.TryLoad(out UserSettingsTarSection section));

                Assert.Equal(7, Assert.Single(section.TarChannelConfigs).Value.RetentionDays);
            }
            finally
            {
                DeleteSettingsFile(filePath);
            }
        }

        [Fact]
        public void Save_NormalizesAndMergesTarSectionWithoutDroppingUnrelatedSettings()
        {
            string filePath = TemporarySettingsPath();
            try
            {
                File.WriteAllText(filePath, "{\"KeepMe\":{\"Nested\":123},\"Other\":\"value\"}");
                var persistence = new TarSettingsPersistence(new SettingsSectionStore(filePath));
                var configs = new Dictionary<string, TarChannelConfig>
                {
                    [" sys|42 "] = new TarChannelConfig
                    {
                        Enabled = true,
                        RetentionDays = -4,
                        IgnoredSubscriberIds = new List<uint> { 9, 2, 9, 0 },
                    },
                    ["   "] = new TarChannelConfig { Enabled = true },
                };

                persistence.Save("  /tmp/saved-tar  ", configs);

                var root = JObject.Parse(File.ReadAllText(filePath));
                Assert.Equal(123, (int)root["KeepMe"]!["Nested"]!);
                Assert.Equal("value", (string)root["Other"]!);
                Assert.Equal("/tmp/saved-tar", (string)root[nameof(UserSettingsTarSection.TarRecordingsRootPath)]!);
                JProperty savedConfig = Assert.Single(((JObject)root[nameof(UserSettingsTarSection.TarChannelConfigs)]!).Properties());
                Assert.Equal("sys|42", savedConfig.Name);
                Assert.True((bool)savedConfig.Value![nameof(TarChannelConfig.Enabled)]!);
                Assert.Equal(0, (int)savedConfig.Value[nameof(TarChannelConfig.RetentionDays)]!);
                Assert.Equal(new JArray(2, 9), savedConfig.Value[nameof(TarChannelConfig.IgnoredSubscriberIds)]);
            }
            finally
            {
                DeleteSettingsFile(filePath);
            }
        }

        private static string TemporarySettingsPath()
            => Path.Combine(Path.GetTempPath(), "dvmconsole-tar-settings-" + Guid.NewGuid().ToString("N") + ".json");

        private static void DeleteSettingsFile(string filePath)
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }
}
