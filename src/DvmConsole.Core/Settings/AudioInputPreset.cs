// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Core.Settings;

/// <summary>Immutable gain/EQ preset. AGC and input routing remain independent.</summary>
public sealed record AudioInputPreset(string Name, double Gain, double LowGainDb, double MidGainDb, double HighGainDb)
{
    public static AudioInputPreset Create(string? name, int existingCount, double gain, double low, double mid, double high)
    {
        if (!Within(gain, 0.25, 4) || !Within(low, -12, 12) || !Within(mid, -12, 12) || !Within(high, -12, 12))
            throw new ArgumentException("Microphone presets require gain 0.25–4.0 and EQ values from -12 to 12 dB.");
        string label = string.IsNullOrWhiteSpace(name) ? $"Mic preset {existingCount + 1}" : name.Trim();
        if (label.Length > 80)
            throw new ArgumentException("Microphone preset names must be 80 characters or fewer.");
        return new(label, gain, low, mid, high);
    }

    public static bool NamesEqual(string left, string right) => StringComparer.OrdinalIgnoreCase.Equals(left, right);
    private static bool Within(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
    public static AudioInputPreset FromSetting(AudioInputPresetSetting setting)
        => new(setting.Name, setting.Gain, setting.LowGainDb, setting.MidGainDb, setting.HighGainDb);
    public AudioInputPresetSetting ToSetting() => new()
    { Name = Name, Gain = Gain, LowGainDb = LowGainDb, MidGainDb = MidGainDb, HighGainDb = HighGainDb };
}
