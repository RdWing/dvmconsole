// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;
using DvmConsole.Core.Settings;

namespace DvmConsole.Presentation;

internal static class ToneEditorSequences
{
    public static GeneratedToneSequence Dtmf(string normalizedDigits)
    {
        List<GeneratedToneStep> steps = [];
        foreach (char digit in normalizedDigits)
        {
            if (steps.Count > 0) steps.Add(GeneratedToneStep.Silence(TimeSpan.FromMilliseconds(60)));
            steps.Add(GeneratedToneStep.Dtmf(digit, TimeSpan.FromMilliseconds(240)));
        }
        return new(steps);
    }
    public static GeneratedToneSequence DtmfPreset(DtmfPresetViewModel preset)
        => new(preset.Steps.Select(step => string.Equals(step.Kind, AudioPresetStepKinds.Hold, StringComparison.OrdinalIgnoreCase)
            ? GeneratedToneStep.Silence(TimeSpan.FromSeconds(step.DurationSeconds))
            : GeneratedToneStep.Dtmf(string.IsNullOrWhiteSpace(step.Digit) ? '1' : step.Digit[0], TimeSpan.FromSeconds(step.DurationSeconds))));
    public static GeneratedToneSequence TonePreset(TonePresetViewModel preset)
        => new(preset.Steps.Select(step => string.Equals(step.Kind, AudioPresetStepKinds.Hold, StringComparison.OrdinalIgnoreCase)
            ? GeneratedToneStep.Silence(TimeSpan.FromSeconds(step.DurationSeconds))
            : GeneratedToneStep.Tone(step.FrequencyHz, TimeSpan.FromSeconds(step.DurationSeconds))));
}
