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
    /// RED contract for the Avalonia-facing layout persistence adapter.
    /// Layout values remain owned by the Core DTO; this adapter owns only the
    /// merge-preserving settings-store boundary.
    /// </summary>
    public sealed class LayoutSettingsPersistenceTests
    {
        [Fact]
        public void Adapter_IsPublicSealedAndBoundToCoreLayoutSection()
        {
            var type = typeof(LayoutSettingsPersistence);

            Assert.Equal("DvmConsole.Avalonia.Persistence", type.Namespace);
            Assert.True(type.IsPublic);
            Assert.True(type.IsSealed);
            Assert.NotNull(type.GetConstructor(new[] { typeof(SettingsSectionStore) }));
            Assert.NotNull(type.GetMethod(nameof(LayoutSettingsPersistence.TryLoad)));
            Assert.NotNull(type.GetMethod(nameof(LayoutSettingsPersistence.Save)));
        }

        [Fact]
        public void Constructor_RejectsNullStore()
        {
            Assert.Throws<ArgumentNullException>(() => new LayoutSettingsPersistence(null!));
        }

        [Fact]
        public void SaveThenTryLoad_RoundTripsWpfLayoutValuesAndNullableBackground()
        {
            using var dir = new TempDir();
            var persistence = CreatePersistence(dir.SettingsPath);
            var expected = new UserSettingsLayoutSection
            {
                ChannelPositions = new Dictionary<string, UserSettingsLayoutPosition>
                {
                    ["SYS|Dispatch"] = new UserSettingsLayoutPosition { X = 12.5, Y = 34.75 }
                },
                WebStreamPositions = new Dictionary<string, UserSettingsLayoutPosition>
                {
                    ["News"] = new UserSettingsLayoutPosition { X = 4, Y = 8 }
                },
                Maximized = true,
                WindowWidth = 1200,
                WindowHeight = 900,
                CanvasWidth = 1180,
                CanvasHeight = 840,
                KeepWindowOnTop = true,
                LockWidgets = false,
                ShowAlertTones = false,
                ShowChannels = false,
                ShowSystemStatus = false,
                UserBackgroundImage = null
            };

            persistence.Save(expected);

            Assert.True(persistence.TryLoad(out var actual));
            Assert.True(actual.Maximized);
            Assert.Equal(1200d, actual.WindowWidth);
            Assert.Equal(900d, actual.WindowHeight);
            Assert.Equal(1180d, actual.CanvasWidth);
            Assert.Equal(840d, actual.CanvasHeight);
            Assert.True(actual.KeepWindowOnTop);
            Assert.False(actual.LockWidgets);
            Assert.False(actual.ShowAlertTones);
            Assert.False(actual.ShowChannels);
            Assert.False(actual.ShowSystemStatus);
            Assert.Null(actual.UserBackgroundImage);
            Assert.Equal(12.5, actual.ChannelPositions["SYS|Dispatch"].X);
            Assert.Equal(34.75, actual.ChannelPositions["SYS|Dispatch"].Y);
            Assert.Equal(8d, actual.WebStreamPositions["News"].Y);
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
                  "FneSystems": [{ "Name": "system-1", "Port": 62031 }],
                  "WindowWidth": 300,
                  "WindowHeight": 200
                }
                """);
            var persistence = CreatePersistence(dir.SettingsPath);

            persistence.Save(new UserSettingsLayoutSection
            {
                WindowWidth = 1200,
                WindowHeight = 900,
                UserBackgroundImage = "/tmp/background.png"
            });

            var saved = JObject.Parse(File.ReadAllText(dir.SettingsPath));
            Assert.Equal("USB-MIC-01", (string)saved["AudioInputDeviceKey"]!);
            Assert.Equal("system-1", (string)saved["FneSystems"]![0]!["Name"]!);
            Assert.Equal(62031, (int)saved["FneSystems"]![0]!["Port"]!);
            Assert.Equal(1200, (double)saved["WindowWidth"]!);
            Assert.Equal(900, (double)saved["WindowHeight"]!);
            Assert.Equal("/tmp/background.png", (string)saved["UserBackgroundImage"]!);
        }

        [Fact]
        public void TryLoad_MissingOrMalformedFile_ReturnsFalseWithWpfDefaults()
        {
            using var dir = new TempDir();
            var persistence = CreatePersistence(dir.SettingsPath);

            Assert.False(persistence.TryLoad(out var missing));
            Assert.False(missing.Maximized);
            Assert.Equal(875d, missing.WindowWidth);
            Assert.Equal(700d, missing.WindowHeight);
            Assert.True(missing.LockWidgets);
            Assert.True(missing.ShowAlertTones);
            Assert.True(missing.ShowChannels);
            Assert.True(missing.ShowSystemStatus);
            Assert.Null(missing.UserBackgroundImage);
            Assert.Empty(missing.ChannelPositions);

            File.WriteAllText(dir.SettingsPath, "{ not valid json");
            Assert.False(persistence.TryLoad(out var malformed));
            Assert.False(malformed.Maximized);
            Assert.Equal(875d, malformed.WindowWidth);
            Assert.Equal(700d, malformed.WindowHeight);
            Assert.True(malformed.LockWidgets);
            Assert.True(malformed.ShowAlertTones);
            Assert.True(malformed.ShowChannels);
            Assert.True(malformed.ShowSystemStatus);
            Assert.Null(malformed.UserBackgroundImage);
        }

        [Fact]
        public void Save_MalformedFile_PropagatesInsteadOfOverwriting()
        {
            using var dir = new TempDir();
            const string malformed = "{ not valid json";
            File.WriteAllText(dir.SettingsPath, malformed);
            var persistence = CreatePersistence(dir.SettingsPath);

            Assert.ThrowsAny<Exception>(() => persistence.Save(new UserSettingsLayoutSection
            {
                WindowWidth = 1200
            }));
            Assert.Equal(malformed, File.ReadAllText(dir.SettingsPath));
        }

        private static LayoutSettingsPersistence CreatePersistence(string path)
            => new(new SettingsSectionStore(path));

        private sealed class TempDir : IDisposable
        {
            public string Root { get; } = Path.Combine(
                Path.GetTempPath(),
                "dvmconsole-avalonia-layout-persistence-" + Guid.NewGuid().ToString("N"));

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
