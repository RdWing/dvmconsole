// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Audio;
using DvmConsole.Core.Settings;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class AudioInputSettingsControllerTests
{
    [Fact]
    public async Task ApplyCommitsValidatedSettingsAfterRuntimeSucceeds()
    {
        var settings = new UserSettings
        {
            AudioInputDeviceId = "old-input",
            AudioOutputDeviceId = "old-output",
            AudioInputGain = 1
        };
        var workspace = new AudioSettingsViewModel(settings, "DVM Console");
        workspace.AudioInputDeviceIdText = " new-input ";
        workspace.AudioOutputDeviceIdText = " new-output ";
        workspace.AudioInputGainText = "1.5";
        workspace.AudioInputAgcTargetDbfsText = "-20";
        workspace.AudioInputLowGainText = "1";
        workspace.AudioInputMidGainText = "2";
        workspace.AudioInputHighGainText = "3";
        workspace.AudioInputAgcEnabled = true;
        var session = new RecordingAudioSession(settings);
        var controller = new AudioInputSettingsController(workspace, settings, session);

        await controller.ApplyAsync(restartActiveAudio: true);

        Assert.Equal("new-input", settings.AudioInputDeviceId);
        Assert.Equal("new-output", settings.AudioOutputDeviceId);
        Assert.Equal(1.5, settings.AudioInputGain);
        Assert.Equal(-20, settings.AudioInputAgcTargetDbfs);
        Assert.Equal(1, session.PersistCalls);
        Assert.Equal(1, session.ApplyCalls);
        Assert.True(session.LastReconfigureRoute);
        Assert.Equal(1.5, session.UpdatedOptions[^1].Gain);
        Assert.Contains("saved", session.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RuntimeFailureRestoresTransmitOptionsWithoutPersistingEdits()
    {
        var settings = new UserSettings
        {
            AudioInputDeviceId = "old-input",
            AudioOutputDeviceId = "old-output",
            AudioInputGain = 1
        };
        var workspace = new AudioSettingsViewModel(settings, "DVM Console")
        {
            AudioInputDeviceIdText = "new-input",
            AudioOutputDeviceIdText = "new-output",
            AudioInputGainText = "2"
        };
        var session = new RecordingAudioSession(settings)
        {
            ApplyException = new IOException("route unavailable")
        };
        var controller = new AudioInputSettingsController(workspace, settings, session);

        await Assert.ThrowsAsync<IOException>(() => controller.ApplyAsync(restartActiveAudio: true));

        Assert.Equal("old-input", settings.AudioInputDeviceId);
        Assert.Equal("old-output", settings.AudioOutputDeviceId);
        Assert.Equal(0, session.PersistCalls);
        Assert.Collection(
            session.UpdatedOptions,
            proposed => Assert.Equal(2, proposed.Gain),
            restored => Assert.Equal(1, restored.Gain));
    }

    [Fact]
    public async Task EditsMadeWhileApplyRunsRemainPendingAfterSnapshotIsPersisted()
    {
        var settings = new UserSettings
        {
            AudioInputDeviceId = "input",
            AudioOutputDeviceId = "output",
            AudioInputGain = 1
        };
        var workspace = new AudioSettingsViewModel(settings, "DVM Console")
        {
            AudioInputGainText = "1.5"
        };
        var session = new RecordingAudioSession(settings)
        {
            ApplyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            ContinueApply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var controller = new AudioInputSettingsController(workspace, settings, session);

        Task apply = controller.ApplyAsync(restartActiveAudio: true);
        await session.ApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        workspace.AudioInputGainText = "2.25";
        session.ContinueApply.SetResult();
        await apply;

        Assert.Equal(1.5, settings.AudioInputGain);
        Assert.Equal("2.25", workspace.AudioInputGainText);
        Assert.Contains("remain ready to apply", session.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeletedPresetCanBeRestoredThroughOperatorUndo()
    {
        var settings = new UserSettings();
        var workspace = new AudioSettingsViewModel(settings, "DVM Console");
        var preset = new AudioInputPresetViewModel(new AudioInputPresetSetting
        {
            Name = "Field",
            Gain = 1.25
        });
        workspace.MutableAudioInputPresets.Add(preset);
        workspace.AudioInputPresetNameText = preset.Name;
        var session = new RecordingAudioSession(settings);
        var controller = new AudioInputSettingsController(workspace, settings, session);

        controller.DeletePreset(preset);

        Assert.Empty(workspace.MutableAudioInputPresets);
        Assert.NotNull(session.PendingUndo);
        await session.PendingUndo!();
        Assert.Same(preset, Assert.Single(workspace.MutableAudioInputPresets));
        Assert.Equal("Field", workspace.AudioInputPresetNameText);
        Assert.Equal(2, session.PersistCalls);
    }

    private sealed class RecordingAudioSession(UserSettings settings) : IAudioInputSettingsSession
    {
        public bool HasActiveTransmission { get; set; }
        public bool KeepTransmitMicrophoneWarm => settings.KeepTransmitMicrophoneWarm;
        public ApplicationAudioConfiguration CurrentConfiguration => new(
            AudioProcessingMode.DvmConsole,
            settings.AudioInputDeviceId,
            settings.AudioOutputDeviceId);
        public AudioProcessingMode SelectedProcessingMode => AudioProcessingMode.DvmConsole;
        public List<AudioInputProcessingOptions> UpdatedOptions { get; } = [];
        public Exception? ApplyException { get; init; }
        public TaskCompletionSource? ApplyStarted { get; init; }
        public TaskCompletionSource? ContinueApply { get; init; }
        public int ApplyCalls { get; private set; }
        public int PersistCalls { get; private set; }
        public bool LastReconfigureRoute { get; private set; }
        public string Status { get; private set; } = string.Empty;
        public Func<ValueTask>? PendingUndo { get; private set; }

        public void UpdateTransmitInputOptions(AudioInputProcessingOptions options)
            => UpdatedOptions.Add(options);

        public async Task ApplyRuntimeSettingsAsync(
            bool reconfigureRoute,
            ApplicationAudioConfiguration previousConfiguration,
            ApplicationAudioConfiguration proposedConfiguration,
            bool restoreWarmMicrophone)
        {
            ApplyCalls++;
            LastReconfigureRoute = reconfigureRoute;
            ApplyStarted?.SetResult();
            if (ContinueApply is not null)
                await ContinueApply.Task;
            if (ApplyException is not null)
                throw ApplyException;
        }

        public void PersistUserSettings() => PersistCalls++;
        public void SetAudioStatus(string status) => Status = status;
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
