// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Settings;
using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ToneWorkspaceViewModelTests
{
    [Fact]
    public async Task ToneAdmissionRejectsSharedPttStartupWithoutQueueingThePress()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvm-tone-admission", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var owner = MainWindowViewModel.Load(
                Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml"),
                new UserSettingsStore(Path.Combine(root, "settings.json")),
                uiDispatcher: new QueuedTestUiDispatcher(), networkDisabledDemo: true);
            ChannelViewModel channel = owner.Systems[0].Channels[0];
            var tones = (DvmConsole.Application.IGeneratedAudioOperationPort)owner;
            await using (await tones.EnterTransmitAsync(default))
            {
                // Exercise the shared startup used by channel controls and hardware
                // after their input-specific selection/availability checks.
                var startup = (IHardwarePttSequencePort)owner;
                await startup.StartTargetsAsync([channel.Id]).WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Contains("another transmit operation", owner.TransmitStatusText);
                Assert.False(channel.IsTransmitting);
            }
            await ((IHardwarePttSequencePort)owner).StartTargetsAsync([channel.Id]);
            Assert.Contains("Demo safety boundary", owner.TransmitStatusText);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task TargetSummariesFollowTheSendResolversAcrossSystemsAndDirectSelectionChanges()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvm-tone-targets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var dispatcher = new QueuedTestUiDispatcher();
            await using var owner = MainWindowViewModel.Load(
                Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml"),
                new UserSettingsStore(Path.Combine(root, "settings.json")), uiDispatcher: dispatcher, networkDisabledDemo: true);
            var presentation = (DvmConsole.Presentation.IToneSettingsViewModel)owner;
            var changes = new List<string?>();
            owner.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
            Assert.Equal("ALERT: 0 armed", presentation.AlertTargetSummary);
            Assert.Contains("Enable ALERT", presentation.AlertTargetDetails);
            ChannelViewModel first = owner.Systems[0].Channels[0];
            ChannelViewModel second = owner.Systems[1].Channels[0];
            first.SetAlertSelected(true);
            second.SetAlertSelected(true);
            first.SetPageSelected(true);
            dispatcher.RunPending();
            Assert.Equal($"ALERT: {owner.ResolveGeneratedToneChannels().Length} armed", presentation.AlertTargetSummary);
            Assert.Contains($"{first.SystemName} / {first.Name}", presentation.AlertTargetDetails);
            Assert.Contains($"{second.SystemName} / {second.Name}", presentation.AlertTargetDetails);
            Assert.Equal("PAGE: 1 armed", presentation.PageTargetSummary);
            first.SetPageSelected(false);
            second.SetAlertSelected(false);
            dispatcher.RunPending();
            Assert.Equal("PAGE: 0 armed", presentation.PageTargetSummary);
            Assert.Equal("ALERT: 1 armed", presentation.AlertTargetSummary);
            Assert.Contains(nameof(presentation.AlertTargetSummary), changes);
            Assert.Contains(nameof(presentation.PageTargetSummary), changes);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void InitializesEditableToneStateAndStableCollectionsFromSettings()
    {
        var settings = new UserSettings
        {
            LastDtmfDigits = "12#",
            ToneFrequencyHz = 1_234.5,
            ToneDurationSeconds = 0.75,
            QuickCallToneAFrequencyHz = 600,
            QuickCallToneBFrequencyHz = 1_200
        };
        var workspace = new ToneWorkspaceViewModel(settings);
        string? changedProperty = null;
        workspace.PropertyChanged += (_, args) => changedProperty = args.PropertyName;

        workspace.DtmfDigits = "9*";

        Assert.Equal(nameof(ToneWorkspaceViewModel.DtmfDigits), changedProperty);
        Assert.Equal("1234.5", workspace.ToneFrequencyText);
        Assert.Equal("0.75", workspace.ToneDurationText);
        Assert.Single(workspace.ToneSequenceSteps);
        Assert.Equal(3, workspace.BuiltInAlertTones.Count);
    }

    [Fact]
    public void FiltersLongPresetListsWithoutChangingPersistedOrder()
    {
        var settings = new UserSettings
        {
            DtmfPresets = Enumerable.Range(1, 7)
                .Select(index => new DtmfPresetSetting
                {
                    Name = index == 4 ? "Regional dispatch" : $"Preset {index}",
                    Digits = index.ToString()
                })
                .ToList()
        };
        var workspace = new ToneWorkspaceViewModel(settings);

        workspace.DtmfPresetFilterText = "regional";

        Assert.True(workspace.IsDtmfPresetFilterVisible);
        Assert.Equal("Regional dispatch", Assert.Single(workspace.FilteredDtmfPresets).Name);
        Assert.Equal(7, workspace.DtmfPresets.Count);
        Assert.Equal("Preset 1", workspace.DtmfPresets[0].Name);
    }

    [Fact]
    public void MicrophonePresetFilterTracksSourceChanges()
    {
        var settings = new UserSettings
        {
            AudioInputPresets = Enumerable.Range(1, 6)
                .Select(index => new AudioInputPresetSetting { Name = $"Mic {index}" })
                .ToList()
        };
        var workspace = new AudioSettingsViewModel(settings, "DVM Console processing");
        workspace.AudioInputPresetFilterText = "field";

        workspace.MutableAudioInputPresets.Add(new AudioInputPresetViewModel(
            new AudioInputPresetSetting { Name = "Field microphone" }));

        Assert.True(workspace.IsAudioInputPresetFilterVisible);
        Assert.Equal("Field microphone", Assert.Single(workspace.FilteredAudioInputPresets).Name);
    }
}
