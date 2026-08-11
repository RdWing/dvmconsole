// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.IO;
using System.Linq;
using DvmConsole.Avalonia.Persistence;
using DvmConsole.Avalonia.ViewModels;
using dvmconsole;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for the headless operator-preferences VM and its
    /// post-hydration dashboard persistence boundary.
    /// </summary>
    public sealed class MainWindowPreferencesCompositionTests
    {
        private sealed class TempDir : IDisposable
        {
            public string Root { get; } = Path.Combine(
                Path.GetTempPath(),
                "dvmconsole-main-window-preferences-" + Guid.NewGuid().ToString("N"));

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
        public void LeafViewModel_ExposesExactlySixChangeOnlyPreferenceProperties()
        {
            var type = typeof(OperatorPreferencesViewModel);
            var properties = type.GetProperties();

            Assert.Equal("DvmConsole.Avalonia.ViewModels", type.Namespace);
            Assert.True(type.IsPublic);
            Assert.True(type.IsSealed);
            Assert.Equal(6, properties.Length);
            Assert.All(properties, property => Assert.Equal(typeof(bool), property.PropertyType));
            Assert.Equal(
                new[]
                {
                    "TalkPermitTone",
                    "MuteRxAudioWhileTransmitting",
                    "RetainPatchStateOnStartup",
                    "RestoreSelectedChannelsOnStartup",
                    "DarkMode",
                    "KeepWindowOnTop",
                },
                properties.Select(property => property.Name));
        }

        [Fact]
        public void FullConstructor_HydratesAllPreferencesBeforeDashboardUse()
        {
            using var dir = new TempDir();
            var persistence = CreatePersistence(dir.SettingsPath);
            persistence.Save(new UserSettingsPreferencesSection
            {
                TalkPermitTone = true,
                MuteRxAudioWhileTransmitting = true,
                RetainPatchStateOnStartup = true,
                RestoreSelectedChannelsOnStartup = true,
                DarkMode = true,
                KeepWindowOnTop = true,
            });

            var vm = CreateViewModel(persistence);

            Assert.NotNull(vm.Preferences);
            Assert.True(vm.Preferences!.TalkPermitTone);
            Assert.True(vm.Preferences.MuteRxAudioWhileTransmitting);
            Assert.True(vm.Preferences.RetainPatchStateOnStartup);
            Assert.True(vm.Preferences.RestoreSelectedChannelsOnStartup);
            Assert.True(vm.Preferences.DarkMode);
            Assert.True(vm.Preferences.KeepWindowOnTop);
        }

        [Fact]
        public void MalformedLoad_DegradesToPreferenceDefaultsWithoutThrowing()
        {
            using var dir = new TempDir();
            File.WriteAllText(dir.SettingsPath, "{ not valid json");

            var exception = Record.Exception(() =>
            {
                var vm = CreateViewModel(CreatePersistence(dir.SettingsPath));

                Assert.NotNull(vm.Preferences);
                Assert.False(vm.Preferences!.TalkPermitTone);
                Assert.False(vm.Preferences.MuteRxAudioWhileTransmitting);
                Assert.False(vm.Preferences.RetainPatchStateOnStartup);
                Assert.False(vm.Preferences.RestoreSelectedChannelsOnStartup);
                Assert.False(vm.Preferences.DarkMode);
                Assert.False(vm.Preferences.KeepWindowOnTop);
            });

            Assert.Null(exception);
        }

        [Fact]
        public void NullPersistence_LeavesPreferenceSliceAbsent()
        {
            var vm = CreateViewModel(null);

            Assert.Null(vm.Preferences);
            Assert.Empty(vm.PreferencesSaveFeedback);
        }

        [Fact]
        public void AttachPreferencesPersistence_NotifiesTheShellWhenLeafAppears()
        {
            var vm = CreateViewModel(null);
            var sawPreferencesNotification = false;
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainWindowViewModel.Preferences))
                {
                    sawPreferencesNotification = true;
                }
            };

            using var dir = new TempDir();
            vm.AttachPreferencesPersistence(CreatePersistence(dir.SettingsPath));

            Assert.NotNull(vm.Preferences);
            Assert.True(sawPreferencesNotification);
        }

        [Fact]
        public void Hydration_DoesNotWriteTheSettingsFile()
        {
            using var dir = new TempDir();
            var persistence = CreatePersistence(dir.SettingsPath);
            persistence.Save(new UserSettingsPreferencesSection
            {
                TalkPermitTone = true,
                DarkMode = true,
            });
            File.AppendAllText(dir.SettingsPath, "\n");
            var before = File.ReadAllBytes(dir.SettingsPath);

            _ = CreateViewModel(persistence);

            Assert.Equal(before, File.ReadAllBytes(dir.SettingsPath));
        }

        [Fact]
        public void LeafChangesRaiseSaveAndPropertyChangedOnlyForEffectiveValues()
        {
            var preferences = new OperatorPreferencesViewModel(null);
            var propertyChanges = 0;
            var saves = 0;
            preferences.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(OperatorPreferencesViewModel.DarkMode))
                {
                    propertyChanges++;
                }
            };
            preferences.SaveRequested += () => saves++;

            preferences.DarkMode = false;
            preferences.DarkMode = true;
            preferences.DarkMode = true;

            Assert.True(preferences.DarkMode);
            Assert.Equal(1, propertyChanges);
            Assert.Equal(1, saves);
        }

        [Fact]
        public void EffectiveChange_SavesThroughAdapterPreservesUnrelatedValuesAndAcknowledges()
        {
            using var dir = new TempDir();
            File.WriteAllText(
                dir.SettingsPath,
                """
                {
                  "TalkPermitTone": false,
                  "MuteRxAudioWhileTransmitting": false,
                  "RetainPatchStateOnStartup": false,
                  "RestoreSelectedChannelsOnStartup": false,
                  "DarkMode": false,
                  "KeepWindowOnTop": false,
                  "FneSystems": [{ "Name": "system-1", "Port": 62031 }],
                  "WindowLayout": { "Width": 1200, "Height": 800 },
                  "UnknownScalar": "preserve-me"
                }
                """);
            var vm = CreateViewModel(CreatePersistence(dir.SettingsPath));

            vm.Preferences!.TalkPermitTone = true;

            Assert.Equal("Preferences settings saved", vm.PreferencesSaveFeedback);
            var saved = JObject.Parse(File.ReadAllText(dir.SettingsPath));
            Assert.True((bool)saved["TalkPermitTone"]!);
            Assert.Equal("preserve-me", (string)saved["UnknownScalar"]!);
            Assert.Equal("system-1", (string)saved["FneSystems"]![0]!["Name"]!);
            Assert.Equal(62031, (int)saved["FneSystems"]![0]!["Port"]!);
            Assert.Equal(1200, (int)saved["WindowLayout"]!["Width"]!);
            Assert.Equal(800, (int)saved["WindowLayout"]!["Height"]!);
        }

        [Fact]
        public void MalformedSave_IsolatedAndSurfacesFailureFeedback()
        {
            using var dir = new TempDir();
            const string malformed = "{ not valid json";
            File.WriteAllText(dir.SettingsPath, malformed);
            var vm = CreateViewModel(CreatePersistence(dir.SettingsPath));

            var exception = Record.Exception(() => vm.Preferences!.DarkMode = true);

            Assert.Null(exception);
            Assert.Equal("Preferences settings save failed.", vm.PreferencesSaveFeedback);
            Assert.Equal(malformed, File.ReadAllText(dir.SettingsPath));
        }

        [Fact]
        public void PreferencesFeedback_IsChangeOnlyAndClearsBeforeNextSuccessfulSave()
        {
            using var dir = new TempDir();
            var vm = CreateViewModel(CreatePersistence(dir.SettingsPath));
            var notifications = 0;
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainWindowViewModel.PreferencesSaveFeedback))
                {
                    notifications++;
                }
            };

            vm.Preferences!.KeepWindowOnTop = true;
            vm.Preferences.KeepWindowOnTop = true;

            Assert.Equal("Preferences settings saved", vm.PreferencesSaveFeedback);
            Assert.Equal(1, notifications);
        }

        private static MainWindowViewModel CreateViewModel(
            PreferencesSettingsPersistence? persistence)
            => new(
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                persistence);

        private static PreferencesSettingsPersistence CreatePersistence(string path)
            => new(new SettingsSectionStore(path));
    }
}
