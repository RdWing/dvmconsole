// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;
using DvmConsole.Core.Settings;
using System.Globalization;

namespace DvmConsole.Desktop;

internal interface ITonePresentationSession
{
    void PersistUserSettings();
    void SetTransmitStatus(string status);
    void BeginUndoableAction(string message, Func<ValueTask> undo, Func<ValueTask>? commit = null);
}

/// <summary>
/// Owns tone-editor validation and preset persistence independently from the
/// transport-oriented generated-audio session.
/// </summary>
internal sealed class TonePresentationController
{
    private readonly ToneWorkspaceViewModel workspace;
    private readonly UserSettings settings;
    private readonly ITonePresentationSession session;

    public TonePresentationController(
        ToneWorkspaceViewModel workspace,
        UserSettings settings,
        ITonePresentationSession session)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public void SaveDtmfPreset()
    {
        try
        {
            string digits = NormalizeDtmfInput(workspace.DtmfDigits);
            string name = string.IsNullOrWhiteSpace(workspace.DtmfPresetName)
                ? $"DTMF preset {workspace.MutableDtmfPresets.Count + 1}"
                : workspace.DtmfPresetName.Trim();
            if (name.Length > 80)
                throw new ArgumentException("Preset names must be 80 characters or fewer.", nameof(workspace.DtmfPresetName));

            var next = new DtmfPresetViewModel(new DtmfPresetSetting
            {
                Name = name,
                Digits = digits,
                Steps = digits
                    .Select(digit => new DtmfPresetStepSetting
                    {
                        Kind = AudioPresetStepKinds.Digit,
                        Digit = digit.ToString(),
                        DurationSeconds = 0.25
                    })
                    .ToList()
            });
            ReplaceOrAdd(workspace.MutableDtmfPresets, next, item => item.Name, name);
            PersistDtmfPresets();
            workspace.DtmfPresetName = string.Empty;
            session.SetTransmitStatus($"DTMF preset '{name}' saved.");
        }
        catch (Exception exception)
        {
            session.SetTransmitStatus($"DTMF preset unavailable: {exception.Message}");
        }
    }

    public void SaveTonePreset()
    {
        if (!TryBuildToneSequence(out GeneratedToneSequence? sequence, out string? error))
        {
            session.SetTransmitStatus(error!);
            return;
        }

        ToneSequenceStepViewModel firstTone = workspace.MutableToneSequenceSteps.First(step => !step.IsSilence);
        double frequency = double.Parse(firstTone.FrequencyText, CultureInfo.InvariantCulture);
        double durationSeconds = double.Parse(firstTone.DurationText, CultureInfo.InvariantCulture);
        string name = string.IsNullOrWhiteSpace(workspace.TonePresetName)
            ? $"Tone preset {workspace.MutableTonePresets.Count + 1}"
            : workspace.TonePresetName.Trim();
        if (name.Length > 80)
        {
            session.SetTransmitStatus("Preset names must be 80 characters or fewer.");
            return;
        }

        var next = new TonePresetViewModel(new TonePresetSetting
        {
            Name = name,
            FrequencyHz = frequency,
            DurationSeconds = durationSeconds,
            Steps = workspace.MutableToneSequenceSteps.Select(step => new TonePresetStepSetting
            {
                Kind = step.IsSilence ? AudioPresetStepKinds.Hold : AudioPresetStepKinds.Tone,
                FrequencyHz = step.IsSilence
                    ? 0
                    : double.Parse(step.FrequencyText, CultureInfo.InvariantCulture),
                DurationSeconds = double.Parse(step.DurationText, CultureInfo.InvariantCulture)
            }).ToList()
        });
        ReplaceOrAdd(workspace.MutableTonePresets, next, item => item.Name, name);
        PersistTonePresets();
        workspace.TonePresetName = string.Empty;
        session.SetTransmitStatus($"Tone preset '{name}' saved ({sequence!.Duration.TotalSeconds:0.##} sec).");
    }

    public void LoadDtmfPreset(DtmfPresetViewModel preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        workspace.DtmfDigits = preset.Digits;
        session.SetTransmitStatus($"DTMF preset '{preset.Name}' loaded.");
    }

    public void DeleteDtmfPreset(DtmfPresetViewModel preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        int index = workspace.MutableDtmfPresets.IndexOf(preset);
        if (index < 0)
            return;
        workspace.MutableDtmfPresets.RemoveAt(index);
        PersistDtmfPresets();
        session.BeginUndoableAction(
            $"DTMF preset '{preset.Name}' deleted.",
            () =>
            {
                workspace.MutableDtmfPresets.Insert(
                    Math.Min(index, workspace.MutableDtmfPresets.Count),
                    preset);
                PersistDtmfPresets();
                session.SetTransmitStatus($"DTMF preset '{preset.Name}' restored.");
                return ValueTask.CompletedTask;
            });
        session.SetTransmitStatus($"DTMF preset '{preset.Name}' deleted. Undo is available for 8 seconds.");
    }

    public void LoadTonePreset(TonePresetViewModel preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        workspace.MutableToneSequenceSteps.Clear();
        foreach (TonePresetStepSetting step in preset.Steps)
        {
            bool isSilence = string.Equals(
                step.Kind,
                AudioPresetStepKinds.Hold,
                StringComparison.OrdinalIgnoreCase);
            workspace.MutableToneSequenceSteps.Add(new ToneSequenceStepViewModel(
                isSilence ? GeneratedToneStep.MinimumSingleToneFrequencyHz : step.FrequencyHz,
                step.DurationSeconds,
                isSilence));
        }
        session.SetTransmitStatus($"Tone preset '{preset.Name}' loaded.");
    }

    public void AddToneSequenceStep(bool silence)
        => workspace.MutableToneSequenceSteps.Add(new ToneSequenceStepViewModel(
            silence ? GeneratedToneStep.MinimumSingleToneFrequencyHz : settings.ToneFrequencyHz,
            silence ? 0.2 : settings.ToneDurationSeconds,
            silence));

    public void RemoveToneSequenceStep(ToneSequenceStepViewModel step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (workspace.MutableToneSequenceSteps.Count <= 1)
        {
            session.SetTransmitStatus("A custom tone pattern must retain at least one step.");
            return;
        }
        workspace.MutableToneSequenceSteps.Remove(step);
    }

    public void MoveToneSequenceStep(ToneSequenceStepViewModel step, int offset)
    {
        ArgumentNullException.ThrowIfNull(step);
        int current = workspace.MutableToneSequenceSteps.IndexOf(step);
        int next = Math.Clamp(current + offset, 0, workspace.MutableToneSequenceSteps.Count - 1);
        if (current >= 0 && next != current)
            workspace.MutableToneSequenceSteps.Move(current, next);
    }

    public void DeleteTonePreset(TonePresetViewModel preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        int index = workspace.MutableTonePresets.IndexOf(preset);
        if (index < 0)
            return;
        workspace.MutableTonePresets.RemoveAt(index);
        PersistTonePresets();
        session.BeginUndoableAction(
            $"Tone preset '{preset.Name}' deleted.",
            () =>
            {
                workspace.MutableTonePresets.Insert(
                    Math.Min(index, workspace.MutableTonePresets.Count),
                    preset);
                PersistTonePresets();
                session.SetTransmitStatus($"Tone preset '{preset.Name}' restored.");
                return ValueTask.CompletedTask;
            });
        session.SetTransmitStatus($"Tone preset '{preset.Name}' deleted. Undo is available for 8 seconds.");
    }

    public bool TryBuildToneSequence(
        out GeneratedToneSequence? sequence,
        out string? error)
    {
        sequence = null;
        error = null;
        var steps = new List<GeneratedToneStep>(workspace.MutableToneSequenceSteps.Count);
        bool hasTone = false;
        foreach ((ToneSequenceStepViewModel step, int index) in
                 workspace.MutableToneSequenceSteps.Select((step, index) => (step, index)))
        {
            if (!double.TryParse(
                    step.DurationText,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                out double durationSeconds) ||
                !double.IsFinite(durationSeconds) ||
                durationSeconds <= 0 || durationSeconds > 10)
            {
                error = $"Step {index + 1} duration must be greater than 0 and no more than 10 seconds.";
                return false;
            }

            if (step.IsSilence)
            {
                steps.Add(GeneratedToneStep.Silence(TimeSpan.FromSeconds(durationSeconds)));
                continue;
            }

            if (!double.TryParse(
                    step.FrequencyText,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                out double frequency) ||
                !double.IsFinite(frequency) ||
                frequency < GeneratedToneStep.MinimumSingleToneFrequencyHz ||
                frequency > GeneratedToneStep.MaximumSingleToneFrequencyHz)
            {
                error = $"Step {index + 1} frequency must be 300–2500 Hz.";
                return false;
            }

            steps.Add(GeneratedToneStep.Tone(frequency, TimeSpan.FromSeconds(durationSeconds)));
            hasTone = true;
        }

        if (!hasTone)
        {
            error = "A custom tone pattern must contain at least one tone step.";
            return false;
        }

        sequence = new GeneratedToneSequence(steps);
        if (sequence.Duration <= TimeSpan.FromSeconds(30))
            return true;

        sequence = null;
        error = "A custom tone pattern cannot exceed 30 seconds.";
        return false;
    }

    public static string NormalizeDtmfInput(string value)
    {
        string normalized = new string((value ?? string.Empty)
            .Where(character => !char.IsWhiteSpace(character))
            .Select(char.ToUpperInvariant)
            .ToArray());
        if (normalized.Length is 0 or > 64 ||
            normalized.Any(character => !DtmfToneGenerator.IsDigit(character)))
        {
            throw new ArgumentException(
                "DTMF must contain 1–64 digits from 0–9, *, #, or A–D.",
                nameof(value));
        }
        return normalized;
    }

    private void PersistDtmfPresets()
    {
        settings.DtmfPresets = workspace.MutableDtmfPresets
            .Select(ToDtmfPresetSetting)
            .ToList();
        session.PersistUserSettings();
    }

    private void PersistTonePresets()
    {
        settings.TonePresets = workspace.MutableTonePresets
            .Select(ToTonePresetSetting)
            .ToList();
        session.PersistUserSettings();
    }

    private static void ReplaceOrAdd<T>(
        IList<T> items,
        T next,
        Func<T, string> getName,
        string name)
    {
        int existingIndex = items
            .Select((item, index) => (item, index))
            .Where(entry => getName(entry.item).Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.index)
            .DefaultIfEmpty(-1)
            .First();
        if (existingIndex >= 0 && existingIndex < items.Count)
            items[existingIndex] = next;
        else
            items.Add(next);
    }

    private static DtmfPresetSetting ToDtmfPresetSetting(DtmfPresetViewModel preset)
        => new()
        {
            Name = preset.Name,
            Digits = preset.Digits,
            Steps = preset.Steps
                .Select(step => new DtmfPresetStepSetting
                {
                    Kind = step.Kind,
                    Digit = step.Digit,
                    DurationSeconds = step.DurationSeconds
                })
                .ToList()
        };

    private static TonePresetSetting ToTonePresetSetting(TonePresetViewModel preset)
        => new()
        {
            Name = preset.Name,
            FrequencyHz = preset.FrequencyHz,
            DurationSeconds = preset.DurationSeconds,
            Steps = preset.Steps
                .Select(step => new TonePresetStepSetting
                {
                    Kind = step.Kind,
                    FrequencyHz = step.FrequencyHz,
                    DurationSeconds = step.DurationSeconds
                })
                .ToList()
        };
}
