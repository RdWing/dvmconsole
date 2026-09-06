// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Settings;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ToolbarToneAssignmentTests
{
    [Fact]
    public async Task SameNamedCustomAlertsKeepTheirSelectedAssetAcrossReloadAndDoNotRetargetAfterDeletion()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvm-toolbar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new UserSettingsStore(Path.Combine(root, "settings.json"));
        string firstId = Guid.NewGuid().ToString("N");
        string secondId = Guid.NewGuid().ToString("N");
        store.Save(new UserSettings
        {
            AlertTones =
            [
                new() { Name = "Dispatch", AssetId = firstId, FileName = "first.wav" },
                new() { Name = "Dispatch", AssetId = secondId, FileName = "second.wav" }
            ]
        });
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        try
        {
            await using (var owner = MainWindowViewModel.Load(path, store, networkDisabledDemo: true))
                owner.AssignToolbarCustomAlert(owner.BuiltInAlertTones[0], owner.AlertTones[1]);
            Assert.Equal(secondId, store.Load().ToolbarToneAssignments[1].AssetId);
            await using (var owner = MainWindowViewModel.Load(path, store, networkDisabledDemo: true))
                Assert.Same(owner.AlertTones[1], owner.ResolveToolbarCustomAlert(owner.BuiltInAlertTones[0]));

            UserSettings settings = store.Load();
            settings.AlertTones.RemoveAt(1);
            store.Save(settings);
            await using (var owner = MainWindowViewModel.Load(path, store, networkDisabledDemo: true))
            {
                Assert.Null(owner.ResolveToolbarCustomAlert(owner.BuiltInAlertTones[0]));
                await owner.SendBuiltInAlertToneAsync(owner.BuiltInAlertTones[0]);
                Assert.StartsWith("Custom alert 'Dispatch' is unavailable", owner.TransmitStatusText);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void LegacyPathsDisambiguateNamesAndMissingOrMalformedIdentitiesNeverFallBack()
    {
        var first = new AlertToneViewModel(new() { Name = "Dispatch", FilePath = "/first.wav" });
        var second = new AlertToneViewModel(new() { Name = "Dispatch", FilePath = "/second.wav" });
        AlertToneViewModel[] alerts = [first, second];
        Assert.Same(second, ToolbarCustomAlertResolver.Resolve(alerts, "Dispatch", null, "/second.wav"));
        Assert.Null(ToolbarCustomAlertResolver.Resolve(alerts, "Dispatch", null, "/missing.wav"));
        Assert.Null(ToolbarCustomAlertResolver.Resolve(alerts, "Dispatch", "invalid", "/second.wav"));
        Assert.Null(ToolbarCustomAlertResolver.Resolve(alerts, "Dispatch", Guid.NewGuid().ToString(), "/second.wav"));
        Assert.Null(ToolbarCustomAlertResolver.Resolve(alerts, "Dispatch", null, null));
        Assert.Same(second, ToolbarCustomAlertResolver.Resolve([second], "Dispatch", null, null));
    }

    [Fact]
    public async Task MigratingALegacyAudioFileUpgradesItsShortcutIdentity()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvm-toolbar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string audioPath = Path.Combine(root, "legacy.wav");
        // No ALERT channels are armed, so this validates migration without transmitting.
        using (var writer = new BinaryWriter(File.Create(audioPath)))
        {
            writer.Write("RIFF"u8.ToArray()); writer.Write(40); writer.Write("WAVEfmt "u8.ToArray());
            writer.Write(16); writer.Write((short)1); writer.Write((short)1);
            writer.Write(8000); writer.Write(16000); writer.Write((short)2); writer.Write((short)16);
            writer.Write("data"u8.ToArray()); writer.Write(4);
            writer.Write((short)0); writer.Write((short)0);
        }
        var store = new UserSettingsStore(Path.Combine(root, "settings.json"));
        store.Save(new UserSettings { AlertTones = [new() { Name = "Legacy", FilePath = audioPath }] });
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        try
        {
            await using (var owner = MainWindowViewModel.Load(path, store, networkDisabledDemo: true))
            {
                owner.AssignToolbarCustomAlert(owner.BuiltInAlertTones[0], owner.AlertTones[0]);
                Assert.Equal(audioPath, owner.BuiltInAlertTones[0].AssignedFilePath);
                await owner.SendBuiltInAlertToneAsync(owner.BuiltInAlertTones[0]);
                Assert.True(Guid.TryParse(owner.AlertTones[0].AssetId, out _));
                Assert.Same(owner.AlertTones[0], owner.ResolveToolbarCustomAlert(owner.BuiltInAlertTones[0]));
            }
            var restored = store.Load();
            Assert.Equal(restored.AlertTones[0].AssetId, restored.ToolbarToneAssignments[1].AssetId);
            Assert.Null(restored.ToolbarToneAssignments[1].FilePath);
            Assert.True(File.Exists(audioPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("Station 12 QCII", "STATION")]
    [InlineData("Dispatch alert", "DISPATC")]
    [InlineData("Fire", "FIRE")]
    [InlineData("A very long name", "A")]
    public void ButtonShowsFirstWordCappedAtSevenCharacters(string name, string expected)
    {
        var button = new BuiltInAlertToneViewModel(DvmConsole.Audio.LegacyAlertTone.Alert1);
        button.Assign(new ToolbarToneAssignmentSetting { PresetName = name });
        Assert.Equal(expected, button.DisplayName);
        Assert.Contains(name, button.Description);
        button.Assign(null);
        Assert.Equal("ALERT 1", button.DisplayName);
    }

    [Fact]
    public async Task AssignmentPersistsAndMissingPresetDoesNotSendDefaultAlert()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvm-toolbar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new UserSettingsStore(Path.Combine(root, "settings.json"));
        store.Save(new UserSettings
        {
            TonePresets = [new() { Name = "Station 12 QCII", FrequencyHz = 600, DurationSeconds = 1 }]
        });
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        try
        {
            await using (var owner = MainWindowViewModel.Load(path, store, networkDisabledDemo: true))
            {
                owner.AssignToolbarTone(owner.BuiltInAlertTones[0], owner.TonePresets[0]);
                Assert.Equal("Station 12 QCII", owner.BuiltInAlertTones[0].Name);
                Assert.Contains("ALERT", owner.BuiltInAlertTones[0].Description);
                await owner.SendBuiltInAlertToneAsync(owner.BuiltInAlertTones[0]);
                Assert.Contains("Arm ALERT", owner.TransmitStatusText);
            }
            await using (var owner = MainWindowViewModel.Load(path, store, networkDisabledDemo: true))
            {
                Assert.Equal("Station 12 QCII", owner.BuiltInAlertTones[0].AssignedPresetName);
                Assert.False(owner.BuiltInAlertTones[0].IsCustomAudio);
                owner.DeleteTonePreset(owner.TonePresets[0]);
                await owner.SendBuiltInAlertToneAsync(owner.BuiltInAlertTones[0]);
                Assert.Contains("is unavailable", owner.TransmitStatusText);
                owner.AssignToolbarTone(owner.BuiltInAlertTones[0], null);
                Assert.Equal("ALERT 1", owner.BuiltInAlertTones[0].Name);
            }
            Assert.Empty(store.Load().ToolbarToneAssignments);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CustomAudioAssignmentUsesAssetPathEvenWhenGeneratedPresetHasSameName()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvm-toolbar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new UserSettingsStore(Path.Combine(root, "settings.json"));
        store.Save(new UserSettings
        {
            TonePresets = [new() { Name = "Dispatch" }],
            AlertTones = [new() { Name = "Dispatch", AssetId = Guid.NewGuid().ToString(), FileName = "dispatch.wav" }]
        });
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        try
        {
            await using (var owner = MainWindowViewModel.Load(path, store, networkDisabledDemo: true))
            {
                owner.AssignToolbarCustomAlert(owner.BuiltInAlertTones[0], owner.AlertTones[0]);
            }
            await using (var owner = MainWindowViewModel.Load(path, store, networkDisabledDemo: true))
            {
                var button = owner.BuiltInAlertTones[0];
                Assert.True(button.IsCustomAudio);
                Assert.Equal("DISPATC", button.DisplayName);
                Assert.Contains("on ALERT channels", button.Description);
                await owner.SendBuiltInAlertToneAsync(button);
                // The intentionally absent audio asset must fail as an asset,
                // rather than send the same-named generated pattern.
                Assert.StartsWith("Alert asset unavailable:", owner.TransmitStatusText);
                owner.AssignToolbarTone(button, owner.TonePresets[0]);
                Assert.False(button.IsCustomAudio);
                await owner.SendBuiltInAlertToneAsync(button);
                Assert.Contains("Arm ALERT", owner.TransmitStatusText);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InvalidAssignmentsAreRemovedAndValidTargetsSurviveSettingsRoundTrip()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvm-toolbar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new UserSettingsStore(Path.Combine(root, "settings.json"));
            store.Save(new UserSettings
            {
                ToolbarToneAssignments = new()
                {
                    [1] = new() { PresetName = "  Dispatch  ", IsCustomAudio = true, AssetId = "00112233445566778899aabbccddeeff" },
                    [2] = new() { PresetName = " " },
                    [4] = new() { PresetName = "Outside toolbar" }
                }
            });
            var restored = store.Load();
            Assert.Single(restored.ToolbarToneAssignments);
            Assert.Equal("Dispatch", restored.ToolbarToneAssignments[1].PresetName);
            Assert.True(restored.ToolbarToneAssignments[1].IsCustomAudio);
            var importedStore = new UserSettingsStore(Path.Combine(root, "imported.json"));
            UserSettings imported = importedStore.Import(store.Path, SettingsImportScope.Presets);
            Assert.Equal(restored.ToolbarToneAssignments[1].AssetId, imported.ToolbarToneAssignments[1].AssetId);
            Assert.Equal(restored.ToolbarToneAssignments[1].AssetId, importedStore.Load().ToolbarToneAssignments[1].AssetId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
