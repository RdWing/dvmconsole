// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using DvmConsole.Avalonia.Persistence;
using DvmConsole.Platform.Audio;
using dvmconsole;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract gate for the Avalonia-facing audio persistence adapter:
    /// Core section keys map to platform device ids without moving the mapper
    /// into the dependency-free Platform assembly.
    /// </summary>
    public sealed class AudioSettingsPersistenceTests
    {
        private sealed class TempDir : IDisposable
        {
            public string Root { get; } = Path.Combine(
                Path.GetTempPath(),
                "dvmconsole-avalonia-audio-persistence-" + Guid.NewGuid().ToString("N"));

            public TempDir() => Directory.CreateDirectory(Root);

            public string SettingsPath => Path.Combine(Root, "UserSettings.json");

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
        public void Adapter_IsPublicSealedAndBoundToCoreSectionStore()
        {
            var type = typeof(AudioSettingsPersistence);

            Assert.Equal("DvmConsole.Avalonia.Persistence", type.Namespace);
            Assert.True(type.IsPublic);
            Assert.True(type.IsSealed);
            Assert.NotNull(type.GetConstructor(new[] { typeof(SettingsSectionStore) }));
            Assert.NotNull(type.GetMethod(nameof(AudioSettingsPersistence.TryLoad)));
            Assert.NotNull(type.GetMethod(nameof(AudioSettingsPersistence.Save)));
            Assert.NotNull(type.GetMethod(nameof(AudioSettingsPersistence.ToAudioDeviceId)));
            Assert.NotNull(type.GetMethod(nameof(AudioSettingsPersistence.ToSettingsKey)));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("WINDOWS-DEFAULT")]
        [InlineData("windows-default")]
        public void DefaultSectionKeys_MapToThePlatformDefaultId(string? key)
        {
            Assert.Equal(AudioDeviceId.Default, AudioSettingsPersistence.ToAudioDeviceId(key));
        }

        [Fact]
        public void NonDefaultSectionKey_IsTrimmedIntoOpaqueDeviceId()
        {
            var id = AudioSettingsPersistence.ToAudioDeviceId("  USB-MIC-01  ");

            Assert.Equal(AudioDeviceId.FromKey("USB-MIC-01"), id);
            Assert.Equal("USB-MIC-01", AudioSettingsPersistence.ToSettingsKey(id));
        }

        [Fact]
        public void DefaultId_MapsToTheWpfCompatibleDefaultKey()
        {
            Assert.Equal("windows-default", AudioSettingsPersistence.ToSettingsKey(AudioDeviceId.Default));
        }

        [Fact]
        public void SaveThenTryLoad_RoundTripsTheCoreSection()
        {
            using var dir = new TempDir();
            var persistence = CreatePersistence(dir.SettingsPath);
            var expected = new UserSettingsAudioSection
            {
                AudioInputDeviceKey = "USB-MIC-01",
                MasterOutputDeviceKey = "USB-SPK-02",
                AudioInputAgcEnabled = true
            };

            persistence.Save(expected);

            Assert.True(persistence.TryLoad(out var actual));
            Assert.Equal(expected.AudioInputDeviceKey, actual.AudioInputDeviceKey);
            Assert.Equal(expected.MasterOutputDeviceKey, actual.MasterOutputDeviceKey);
            Assert.Equal(expected.AudioInputAgcEnabled, actual.AudioInputAgcEnabled);
        }

        [Fact]
        public void Save_PreservesUnrelatedWpfShapedFields()
        {
            using var dir = new TempDir();
            File.WriteAllText(
                dir.SettingsPath,
                """
                {
                  "AudioInputDeviceKey": "old-input",
                  "MasterOutputDeviceKey": "old-output",
                  "AudioInputAgcEnabled": false,
                  "FneSystems": [{ "Name": "system-1", "Port": 62031 }],
                  "WindowLayout": { "Width": 1200, "Height": 800 }
                }
                """);
            var persistence = CreatePersistence(dir.SettingsPath);

            persistence.Save(new UserSettingsAudioSection
            {
                AudioInputDeviceKey = "new-input",
                MasterOutputDeviceKey = "new-output",
                AudioInputAgcEnabled = true
            });

            var saved = JObject.Parse(File.ReadAllText(dir.SettingsPath));
            Assert.Equal("new-input", (string)saved["AudioInputDeviceKey"]!);
            Assert.Equal("new-output", (string)saved["MasterOutputDeviceKey"]!);
            Assert.True((bool)saved["AudioInputAgcEnabled"]!);
            Assert.Equal("system-1", (string)saved["FneSystems"]![0]!["Name"]!);
            Assert.Equal(62031, (int)saved["FneSystems"]![0]!["Port"]!);
            Assert.Equal(1200, (int)saved["WindowLayout"]!["Width"]!);
            Assert.Equal(800, (int)saved["WindowLayout"]!["Height"]!);
        }

        [Fact]
        public void TryLoad_MalformedFile_ReturnsFalseWithDefaults()
        {
            using var dir = new TempDir();
            File.WriteAllText(dir.SettingsPath, "{ not valid json");
            var persistence = CreatePersistence(dir.SettingsPath);

            Assert.False(persistence.TryLoad(out var section));
            Assert.Equal("windows-default", section.AudioInputDeviceKey);
            Assert.Equal("windows-default", section.MasterOutputDeviceKey);
            Assert.False(section.AudioInputAgcEnabled);
        }

        [Fact]
        public void Save_MalformedFile_PropagatesInsteadOfOverwriting()
        {
            using var dir = new TempDir();
            const string malformed = "{ not valid json";
            File.WriteAllText(dir.SettingsPath, malformed);
            var persistence = CreatePersistence(dir.SettingsPath);

            Assert.ThrowsAny<Exception>(() => persistence.Save(new UserSettingsAudioSection
            {
                AudioInputDeviceKey = "must-not-write"
            }));
            Assert.Equal(malformed, File.ReadAllText(dir.SettingsPath));
        }

        private static AudioSettingsPersistence CreatePersistence(string path)
            => new(new SettingsSectionStore(path));
    }
}
