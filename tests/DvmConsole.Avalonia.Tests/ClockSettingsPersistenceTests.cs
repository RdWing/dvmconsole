// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using DvmConsole.Avalonia.Persistence;
using dvmconsole;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for the headless Avalonia toolbar-clock persistence
    /// adapter. The Core section owns WPF-compatible values and normalization;
    /// the adapter owns only the merge-preserving store boundary.
    /// </summary>
    public sealed class ClockSettingsPersistenceTests
    {
        [Fact]
        public void Adapter_IsPublicSealedAndBoundToCoreClockSection()
        {
            var type = typeof(ClockSettingsPersistence);

            Assert.Equal("DvmConsole.Avalonia.Persistence", type.Namespace);
            Assert.True(type.IsPublic);
            Assert.True(type.IsSealed);
            Assert.NotNull(type.GetConstructor(new[] { typeof(SettingsSectionStore) }));
            Assert.NotNull(type.GetMethod(nameof(ClockSettingsPersistence.TryLoad)));
            Assert.NotNull(type.GetMethod(nameof(ClockSettingsPersistence.Save)));
        }

        [Fact]
        public void Constructor_RejectsNullStore()
        {
            Assert.Throws<ArgumentNullException>(() => new ClockSettingsPersistence(null!));
        }

        [Fact]
        public void SaveThenTryLoad_RoundTripsNormalizedWpfClockValues()
        {
            using var dir = new TempDir();
            var persistence = CreatePersistence(dir.SettingsPath);
            var expected = new UserSettingsClockSection
            {
                ClockUse24HourTime = false,
                ClockShowSeconds = false,
                ToolbarClockConfigs = new List<ToolbarClockConfig>
                {
                    new ToolbarClockConfig { Enabled = true, UtcOffsetHours = -5, ColorHex = " #0d47a1 " },
                    new ToolbarClockConfig { Enabled = true, UtcOffsetHours = 99, ColorHex = "invalid" }
                }
            };

            persistence.Save(expected);

            Assert.True(persistence.TryLoad(out var actual));
            Assert.False(actual.ClockUse24HourTime);
            Assert.False(actual.ClockShowSeconds);
            Assert.Equal(8, actual.ToolbarClockConfigs.Count);
            Assert.Equal(8, actual.ToolbarClockConfigSlots.Count);
            Assert.True(actual.ToolbarClockConfigs[0].Enabled);
            Assert.Equal(-5, actual.ToolbarClockConfigs[0].UtcOffsetHours);
            Assert.Equal("#0D47A1", actual.ToolbarClockConfigs[0].ColorHex);
            Assert.Equal(14, actual.ToolbarClockConfigs[1].UtcOffsetHours);
            Assert.Equal("#3A3A3A", actual.ToolbarClockConfigs[1].ColorHex);
            Assert.Equal(actual.ToolbarClockConfigs[1].ColorHex, actual.ToolbarClockConfigSlots["2"].ColorHex);
        }

        [Fact]
        public void TryLoad_MissingOrMalformedFile_ReturnsFalseWithWpfDefaults()
        {
            using var dir = new TempDir();
            var persistence = CreatePersistence(dir.SettingsPath);

            Assert.False(persistence.TryLoad(out var missing));
            Assert.True(missing.ClockUse24HourTime);
            Assert.True(missing.ClockShowSeconds);
            Assert.Equal(8, missing.ToolbarClockConfigs.Count);
            Assert.Equal(8, missing.ToolbarClockConfigSlots.Count);
            Assert.All(missing.ToolbarClockConfigs, AssertDefaultClock);

            File.WriteAllText(dir.SettingsPath, "{ not valid json");
            Assert.False(persistence.TryLoad(out var malformed));
            Assert.True(malformed.ClockUse24HourTime);
            Assert.True(malformed.ClockShowSeconds);
            Assert.Equal(8, malformed.ToolbarClockConfigs.Count);
            Assert.Equal(8, malformed.ToolbarClockConfigSlots.Count);
            Assert.All(malformed.ToolbarClockConfigSlots.Values, AssertDefaultClock);
        }

        [Fact]
        public void TryLoad_WpfShapedSlotsUseCaseInsensitiveSlotKeysAndPreserveUnrelatedFields()
        {
            using var dir = new TempDir();
            File.WriteAllText(
                dir.SettingsPath,
                """
                {
                  "AudioInputDeviceKey": "USB-MIC-01",
                  "WindowWidth": 1200,
                  "ToolbarClockConfigs": [{ "Enabled": false, "UtcOffsetHours": 2, "ColorHex": "#111111" }],
                  "ToolbarClockConfigSlots": { "1": { "Enabled": true, "UtcOffsetHours": -5, "ColorHex": "#0d47a1" } },
                  "ClockUse24HourTime": false,
                  "ClockShowSeconds": true
                }
                """);
            var persistence = CreatePersistence(dir.SettingsPath);

            Assert.True(persistence.TryLoad(out var actual));
            Assert.False(actual.ClockUse24HourTime);
            Assert.True(actual.ClockShowSeconds);
            Assert.True(actual.ToolbarClockConfigs[0].Enabled);
            Assert.Equal(-5, actual.ToolbarClockConfigs[0].UtcOffsetHours);
            Assert.Equal("#0D47A1", actual.ToolbarClockConfigs[0].ColorHex);
        }

        [Fact]
        public void Save_PreservesUnrelatedWpfShapedFields()
        {
            using var dir = new TempDir();
            File.WriteAllText(
                dir.SettingsPath,
                """
                {
                  "AudioInputDeviceKey": "USB-MIC-01",
                  "WindowWidth": 300,
                  "FneSystems": [{ "Name": "system-1", "Port": 62031 }]
                }
                """);
            var persistence = CreatePersistence(dir.SettingsPath);

            persistence.Save(new UserSettingsClockSection());

            var saved = JObject.Parse(File.ReadAllText(dir.SettingsPath));
            Assert.Equal("USB-MIC-01", (string)saved["AudioInputDeviceKey"]!);
            Assert.Equal(300, (int)saved["WindowWidth"]!);
            Assert.Equal("system-1", (string)saved["FneSystems"]![0]!["Name"]!);
            Assert.Equal(8, ((JArray)saved[nameof(UserSettingsClockSection.ToolbarClockConfigs)]!).Count);
            Assert.Equal(8, ((JObject)saved[nameof(UserSettingsClockSection.ToolbarClockConfigSlots)]!).Properties().Count());
        }

        [Fact]
        public void Save_MalformedFile_PropagatesInsteadOfOverwriting()
        {
            using var dir = new TempDir();
            const string malformed = "{ not valid json";
            File.WriteAllText(dir.SettingsPath, malformed);
            var persistence = CreatePersistence(dir.SettingsPath);

            Assert.ThrowsAny<Exception>(() => persistence.Save(new UserSettingsClockSection()));
            Assert.Equal(malformed, File.ReadAllText(dir.SettingsPath));
        }

        private static ClockSettingsPersistence CreatePersistence(string path)
            => new(new SettingsSectionStore(path));

        private static void AssertDefaultClock(ToolbarClockConfig config)
        {
            Assert.False(config.Enabled);
            Assert.Equal(0, config.UtcOffsetHours);
            Assert.Equal(UserSettingsClockSection.DEFAULT_TOOLBAR_CLOCK_COLOR, config.ColorHex);
        }

        private sealed class TempDir : IDisposable
        {
            public string Root { get; } = Path.Combine(
                Path.GetTempPath(),
                "dvmconsole-avalonia-clock-persistence-" + Guid.NewGuid().ToString("N"));

            public TempDir()
            {
                Directory.CreateDirectory(Root);
            }

            public string SettingsPath => Path.Combine(Root, "UserSettings.json");

            public void Dispose()
            {
                try
                {
                    if (Directory.Exists(Root))
                        Directory.Delete(Root, recursive: true);
                }
                catch (IOException)
                {
                    // Best-effort cleanup; never mask the test result.
                }
            }
        }
    }
}
