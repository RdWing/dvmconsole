// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Audio;
using DvmConsole.Core.Settings;
using System.Globalization;

namespace DvmConsole.Desktop;

internal interface IAudioInputSettingsSession
{
    bool HasActiveTransmission { get; }
    bool KeepTransmitMicrophoneWarm { get; }
    ApplicationAudioConfiguration CurrentConfiguration { get; }
    AudioProcessingMode SelectedProcessingMode { get; }

    void UpdateTransmitInputOptions(AudioInputProcessingOptions options);
    Task ApplyRuntimeSettingsAsync(
        bool reconfigureRoute,
        ApplicationAudioConfiguration previousConfiguration,
        ApplicationAudioConfiguration proposedConfiguration,
        bool restoreWarmMicrophone);
    void PersistUserSettings();
    void SetAudioStatus(string status);
    void BeginUndoableAction(string message, Func<ValueTask> undo, Func<ValueTask>? commit = null);
}

/// <summary>
/// Owns microphone-setting validation, persistence, and runtime application.
/// The bindable workspace remains presentation state; platform/session work is
/// supplied through the narrow session port.
/// </summary>
internal sealed class AudioInputSettingsController
{
    private sealed record Draft(
        long EditRevision,
        string InputDeviceId,
        string OutputDeviceId,
        AudioProcessingMode ProcessingMode,
        bool AgcEnabled,
        double AgcTargetDbfs,
        double Gain,
        double LowGainDb,
        double MidGainDb,
        double HighGainDb)
    {
        public ApplicationAudioConfiguration Configuration
            => new(ProcessingMode, InputDeviceId, OutputDeviceId);

        public AudioInputProcessingOptions InputOptions
            => CreateInputOptions(
                InputDeviceId,
                ProcessingMode,
                AgcEnabled,
                AgcTargetDbfs,
                Gain,
                LowGainDb,
                MidGainDb,
                HighGainDb);
    }

    private readonly AudioSettingsViewModel workspace;
    private readonly UserSettings settings;
    private readonly IAudioInputSettingsSession session;

    public AudioInputSettingsController(
        AudioSettingsViewModel workspace,
        UserSettings settings,
        IAudioInputSettingsSession session)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public void SavePreset()
    {
        if (!TryParseBounded(workspace.AudioInputGainText, 0.25, 3.0, out double gain) ||
            !TryParseBounded(workspace.AudioInputLowGainText, -12, 12, out double lowGainDb) ||
            !TryParseBounded(workspace.AudioInputMidGainText, -12, 12, out double midGainDb) ||
            !TryParseBounded(workspace.AudioInputHighGainText, -12, 12, out double highGainDb))
        {
            session.SetAudioStatus("Microphone presets require gain 0.25–3.0 and EQ values from -12 to 12 dB.");
            return;
        }

        string name = string.IsNullOrWhiteSpace(workspace.AudioInputPresetNameText)
            ? $"Mic preset {workspace.MutableAudioInputPresets.Count + 1}"
            : workspace.AudioInputPresetNameText.Trim();
        if (name.Length > 80)
        {
            session.SetAudioStatus("Microphone preset names must be 80 characters or fewer.");
            return;
        }

        var next = new AudioInputPresetViewModel(new AudioInputPresetSetting
        {
            Name = name,
            Gain = gain,
            LowGainDb = lowGainDb,
            MidGainDb = midGainDb,
            HighGainDb = highGainDb
        });
        int existingIndex = workspace.MutableAudioInputPresets
            .Select((preset, index) => (preset, index))
            .Where(item => item.preset.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .DefaultIfEmpty(-1)
            .First();
        if (existingIndex >= 0 && existingIndex < workspace.MutableAudioInputPresets.Count)
            workspace.MutableAudioInputPresets[existingIndex] = next;
        else
            workspace.MutableAudioInputPresets.Add(next);

        workspace.AudioInputPresetNameText = name;
        PersistPresetState();
        session.SetAudioStatus($"Microphone preset '{name}' saved.");
    }

    public void LoadPreset(AudioInputPresetViewModel preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        workspace.AudioInputPresetNameText = preset.Name;
        workspace.AudioInputGainText = preset.Gain.ToString("0.###", CultureInfo.InvariantCulture);
        workspace.AudioInputLowGainText = preset.LowGainDb.ToString("0.###", CultureInfo.InvariantCulture);
        workspace.AudioInputMidGainText = preset.MidGainDb.ToString("0.###", CultureInfo.InvariantCulture);
        workspace.AudioInputHighGainText = preset.HighGainDb.ToString("0.###", CultureInfo.InvariantCulture);
        session.SetAudioStatus($"Microphone preset '{preset.Name}' loaded.");
    }

    public void DeletePreset(AudioInputPresetViewModel preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        int index = workspace.MutableAudioInputPresets.IndexOf(preset);
        if (index < 0)
            return;

        string previousName = workspace.AudioInputPresetNameText;
        workspace.MutableAudioInputPresets.RemoveAt(index);

        if (workspace.AudioInputPresetNameText.Equals(preset.Name, StringComparison.OrdinalIgnoreCase))
            workspace.AudioInputPresetNameText = string.Empty;
        PersistPresetState();
        session.BeginUndoableAction(
            $"Microphone preset '{preset.Name}' deleted.",
            () =>
            {
                workspace.MutableAudioInputPresets.Insert(
                    Math.Min(index, workspace.MutableAudioInputPresets.Count),
                    preset);
                workspace.AudioInputPresetNameText = previousName;
                PersistPresetState();
                session.SetAudioStatus($"Microphone preset '{preset.Name}' restored.");
                return ValueTask.CompletedTask;
            });
        session.SetAudioStatus($"Microphone preset '{preset.Name}' deleted. Undo is available for 8 seconds.");
    }

    public async Task ApplyAsync(bool restartActiveAudio)
    {
        if (session.HasActiveTransmission)
        {
            session.SetAudioStatus("Stop transmitting before applying microphone processing settings.");
            return;
        }

        if (!TryCaptureDraft(out Draft? draft) || draft is null)
        {
            session.SetAudioStatus("Microphone settings require a device ID, gain 0.25–3.0, AGC target -40 to -12 dBFS, and EQ values from -12 to 12 dB.");
            return;
        }

        ApplicationAudioConfiguration previousConfiguration = session.CurrentConfiguration;
        AudioInputProcessingOptions previousInputOptions = CreateInputOptions(
            previousConfiguration.InputDeviceId,
            previousConfiguration.ProcessingMode,
            settings.AudioInputAgcEnabled,
            settings.AudioInputAgcTargetDbfs,
            settings.AudioInputGain,
            settings.AudioInputEqLowGainDb,
            settings.AudioInputEqMidGainDb,
            settings.AudioInputEqHighGainDb);
        ApplicationAudioConfiguration proposedConfiguration = draft.Configuration;
        bool audioRouteChanged =
            previousConfiguration.ProcessingMode != proposedConfiguration.ProcessingMode ||
            !previousConfiguration.InputDeviceId.Equals(
                proposedConfiguration.InputDeviceId,
                StringComparison.OrdinalIgnoreCase) ||
            !previousConfiguration.OutputDeviceId.Equals(
                proposedConfiguration.OutputDeviceId,
                StringComparison.OrdinalIgnoreCase);
        AudioInputProcessingOptions proposedInputOptions = draft.InputOptions;

        session.UpdateTransmitInputOptions(proposedInputOptions);
        try
        {
            await session.ApplyRuntimeSettingsAsync(
                restartActiveAudio && audioRouteChanged,
                previousConfiguration,
                proposedConfiguration,
                session.KeepTransmitMicrophoneWarm).ConfigureAwait(false);
        }
        catch
        {
            session.UpdateTransmitInputOptions(previousInputOptions);
            throw;
        }

        settings.AudioInputDeviceId = draft.InputDeviceId;
        settings.AudioOutputDeviceId = draft.OutputDeviceId;
        settings.AudioProcessingMode = draft.ProcessingMode switch
        {
            AudioProcessingMode.WindowsCommunications => UserSettings.WindowsCommunicationsProcessingMode,
            _ => UserSettings.DvmConsoleAudioProcessingMode
        };
        settings.AudioInputAgcEnabled = draft.AgcEnabled;
        settings.AudioInputAgcTargetDbfs = draft.AgcTargetDbfs;
        settings.AudioInputGain = draft.Gain;
        settings.AudioInputEqLowGainDb = draft.LowGainDb;
        settings.AudioInputEqMidGainDb = draft.MidGainDb;
        settings.AudioInputEqHighGainDb = draft.HighGainDb;
        PersistPresetState();

        bool hasNewerEdits = workspace.EditRevision != draft.EditRevision;
        if (!hasNewerEdits)
            NormalizeWorkspace(draft);

        string status = draft.ProcessingMode switch
        {
            AudioProcessingMode.WindowsCommunications =>
                "Windows communications processing saved for microphone transmit capture; available effects depend on Windows and the selected endpoint.",
            _ => "DVM Console audio processing saved; device routes apply to the next audio session and PTT call."
        };
        session.SetAudioStatus(hasNewerEdits
            ? status + " Newer microphone edits remain ready to apply."
            : status);
    }

    private bool TryCaptureDraft(out Draft? draft)
    {
        draft = null;
        string inputDeviceId = workspace.AudioInputDeviceIdText.Trim();
        string outputDeviceId = workspace.AudioOutputDeviceIdText.Trim();
        if (inputDeviceId.Length is 0 or > 256 ||
            outputDeviceId.Length is 0 or > 256 ||
            !TryParseBounded(workspace.AudioInputGainText, 0.25, 3.0, out double gain) ||
            !TryParseBounded(workspace.AudioInputAgcTargetDbfsText, -40, -12, out double agcTargetDbfs) ||
            !TryParseBounded(workspace.AudioInputLowGainText, -12, 12, out double lowGainDb) ||
            !TryParseBounded(workspace.AudioInputMidGainText, -12, 12, out double midGainDb) ||
            !TryParseBounded(workspace.AudioInputHighGainText, -12, 12, out double highGainDb))
        {
            return false;
        }

        draft = new Draft(
            workspace.EditRevision,
            inputDeviceId,
            outputDeviceId,
            session.SelectedProcessingMode,
            workspace.AudioInputAgcEnabled,
            agcTargetDbfs,
            gain,
            lowGainDb,
            midGainDb,
            highGainDb);
        return true;
    }

    private void NormalizeWorkspace(Draft draft)
    {
        workspace.AudioInputDeviceIdText = draft.InputDeviceId;
        workspace.AudioOutputDeviceIdText = draft.OutputDeviceId;
        workspace.AudioInputGainText = draft.Gain.ToString("0.###", CultureInfo.InvariantCulture);
        workspace.AudioInputAgcTargetDbfsText = draft.AgcTargetDbfs.ToString("0.###", CultureInfo.InvariantCulture);
        workspace.AudioInputLowGainText = draft.LowGainDb.ToString("0.###", CultureInfo.InvariantCulture);
        workspace.AudioInputMidGainText = draft.MidGainDb.ToString("0.###", CultureInfo.InvariantCulture);
        workspace.AudioInputHighGainText = draft.HighGainDb.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private void PersistPresetState()
    {
        settings.AudioInputPresetName = workspace.AudioInputPresetNameText.Trim();
        settings.AudioInputPresets = workspace.MutableAudioInputPresets
            .Select(preset => preset.ToSetting())
            .ToList();
        session.PersistUserSettings();
    }

    private static AudioInputProcessingOptions CreateInputOptions(
        string deviceId,
        AudioProcessingMode processingMode,
        bool agcEnabled,
        double agcTargetDbfs,
        double gain,
        double lowGainDb,
        double midGainDb,
        double highGainDb)
        => new()
        {
            DeviceId = deviceId,
            ProcessingMode = processingMode,
            AgcEnabled = agcEnabled,
            AgcTargetDbfs = agcTargetDbfs,
            Gain = gain,
            LowGainDb = lowGainDb,
            MidGainDb = midGainDb,
            HighGainDb = highGainDb
        };

    private static bool TryParseBounded(
        string value,
        double minimum,
        double maximum,
        out double result)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) &&
            double.IsFinite(result) && result >= minimum && result <= maximum;
}
