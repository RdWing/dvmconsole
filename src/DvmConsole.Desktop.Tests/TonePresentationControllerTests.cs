// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Settings;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class TonePresentationControllerTests
{
    [Fact]
    public void SaveTonePresetPersistsValidatedPatternThroughSessionPort()
    {
        var settings = new UserSettings
        {
            ToneFrequencyHz = 1000,
            ToneDurationSeconds = 0.5
        };
        var workspace = new ToneWorkspaceViewModel(settings)
        {
            TonePresetName = "Dispatch alert"
        };
        workspace.MutableToneSequenceSteps.Add(new ToneSequenceStepViewModel(300, 0.2, isSilence: true));
        var session = new RecordingToneSession();
        var controller = new TonePresentationController(workspace, settings, session);

        controller.SaveTonePreset();

        TonePresetSetting preset = Assert.Single(settings.TonePresets);
        Assert.Equal("Dispatch alert", preset.Name);
        Assert.Equal(2, preset.Steps.Count);
        Assert.Equal(AudioPresetStepKinds.Hold, preset.Steps[1].Kind);
        Assert.Equal(1, session.PersistCalls);
        Assert.Contains("saved", session.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidPatternDoesNotPersist()
    {
        var settings = new UserSettings();
        var workspace = new ToneWorkspaceViewModel(settings);
        workspace.MutableToneSequenceSteps[0].FrequencyText = "2600";
        var session = new RecordingToneSession();
        var controller = new TonePresentationController(workspace, settings, session);

        controller.SaveTonePreset();

        Assert.Empty(settings.TonePresets);
        Assert.Equal(0, session.PersistCalls);
        Assert.Contains("300–2500", session.Status, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("NaN", "1")]
    [InlineData("Infinity", "1")]
    [InlineData("1000", "NaN")]
    [InlineData("1000", "Infinity")]
    public void NonFiniteToneValuesDoNotPersist(string frequency, string duration)
    {
        var settings = new UserSettings();
        var workspace = new ToneWorkspaceViewModel(settings);
        workspace.MutableToneSequenceSteps[0].FrequencyText = frequency;
        workspace.MutableToneSequenceSteps[0].DurationText = duration;
        var session = new RecordingToneSession();
        var controller = new TonePresentationController(workspace, settings, session);

        controller.SaveTonePreset();

        Assert.Empty(settings.TonePresets);
        Assert.Equal(0, session.PersistCalls);
    }

    [Fact]
    public void DtmfNormalizationKeepsSupportedDigitsAndRejectsOtherInput()
    {
        Assert.Equal("12#A", TonePresentationController.NormalizeDtmfInput(" 1 2 # a "));
        Assert.Throws<ArgumentException>(() =>
            TonePresentationController.NormalizeDtmfInput("911!"));
    }

    [Fact]
    public async Task DeletedDtmfAndTonePresetsCanBeRestoredThroughOperatorUndo()
    {
        var settings = new UserSettings();
        var workspace = new ToneWorkspaceViewModel(settings);
        var dtmf = new DtmfPresetViewModel(new DtmfPresetSetting
        {
            Name = "Dispatch",
            Digits = "12#"
        });
        var tone = new TonePresetViewModel(new TonePresetSetting
        {
            Name = "Alert",
            FrequencyHz = 1000,
            DurationSeconds = 0.5
        });
        workspace.MutableDtmfPresets.Add(dtmf);
        workspace.MutableTonePresets.Add(tone);
        var session = new RecordingToneSession();
        var controller = new TonePresentationController(workspace, settings, session);

        controller.DeleteDtmfPreset(dtmf);
        Assert.Empty(workspace.MutableDtmfPresets);
        await session.PendingUndo!();
        Assert.Same(dtmf, Assert.Single(workspace.MutableDtmfPresets));

        controller.DeleteTonePreset(tone);
        Assert.Empty(workspace.MutableTonePresets);
        await session.PendingUndo!();
        Assert.Same(tone, Assert.Single(workspace.MutableTonePresets));
        Assert.Equal(4, session.PersistCalls);
    }

    private sealed class RecordingToneSession : ITonePresentationSession
    {
        public int PersistCalls { get; private set; }
        public string Status { get; private set; } = string.Empty;
        public Func<ValueTask>? PendingUndo { get; private set; }

        public void PersistUserSettings() => PersistCalls++;
        public void SetTransmitStatus(string status) => Status = status;
        public void BeginUndoableAction(
            string message,
            Func<ValueTask> undo,
            Func<ValueTask>? commit = null)
        {
            Status = message;
            PendingUndo = undo;
        }
    }
}
