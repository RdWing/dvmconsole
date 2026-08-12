// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using dvmconsole;

namespace DvmConsole.Avalonia.ViewModels
{
    /// <summary>
    /// A selectable resource for a DTMF preset.
    /// </summary>
    public sealed class DtmfPresetTarget
    {
        public DtmfPresetTarget(string key, string displayName)
        {
            Key = key?.Trim() ?? string.Empty;
            DisplayName = string.IsNullOrWhiteSpace(displayName)
                ? Key
                : displayName.Trim();
        }

        public string Key { get; }

        public string DisplayName { get; }
    }

    /// <summary>
    /// Detached generated DTMF PCM request emitted by the manager.
    /// The shell owns playback or transmission of the request.
    /// </summary>
    public sealed class DtmfPresetRequest
    {
        public DtmfPresetRequest(
            string presetId,
            string targetResourceKey,
            byte[] pcm)
        {
            PresetId = presetId ?? string.Empty;
            TargetResourceKey = targetResourceKey ?? string.Empty;
            Pcm = pcm is null ? Array.Empty<byte>() : (byte[])pcm.Clone();
        }

        public string PresetId { get; }

        public string TargetResourceKey { get; }

        public byte[] Pcm { get; }
    }

    /// <summary>
    /// Headless managed state for DTMF preset editing. It owns managed rows,
    /// digit/duration normalization, and detached request payloads; persistence,
    /// playback, and transmission remain shell-owned.
    /// </summary>
    public sealed class DtmfPresetManagerViewModel
    {
        public const double MinimumDurationSeconds = 0.25;
        public const double MaximumDurationSeconds = 10;
        public const string ValidDigits = "0123456789*#ABCD";

        private DtmfPresetItem? selectedPreset;
        private DtmfPresetTarget? selectedTarget;

        public DtmfPresetManagerViewModel(
            IEnumerable<UserSettingsDtmfPresetConfig>? configs,
            IEnumerable<DtmfPresetTarget>? targets)
        {
            Targets = (targets ?? Enumerable.Empty<DtmfPresetTarget>())
                .Where(target => target is not null && !string.IsNullOrWhiteSpace(target.Key))
                .GroupBy(target => target.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            Presets = new ObservableCollection<DtmfPresetItem>(
                (configs ?? Enumerable.Empty<UserSettingsDtmfPresetConfig>())
                    .Where(config => config is not null)
                    .Select(Normalize)
                    .OrderBy(preset => preset.DisplayName, StringComparer.OrdinalIgnoreCase));

            if (Presets.Count > 0)
                SelectedPreset = Presets[0];
            else if (Targets.Count > 0)
                SelectedTarget = Targets[0];
        }

        public ObservableCollection<DtmfPresetItem> Presets { get; }

        public IReadOnlyList<DtmfPresetTarget> Targets { get; }

        public IReadOnlyList<string> StepKinds { get; } = new[] { "digit", "hold" };

        public DtmfPresetItem? SelectedPreset
        {
            get => selectedPreset;
            set
            {
                if (ReferenceEquals(selectedPreset, value))
                    return;

                selectedPreset = value;
                SelectedTarget = ResolveTarget(value);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedPreset)));
            }
        }

        public DtmfPresetTarget? SelectedTarget
        {
            get => selectedTarget;
            set
            {
                if (ReferenceEquals(selectedTarget, value))
                    return;

                selectedTarget = value;
                if (selectedPreset is not null && value is not null)
                    selectedPreset.TargetResourceKey = value.Key;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedTarget)));
            }
        }

        public event Action<IReadOnlyList<UserSettingsDtmfPresetConfig>>? SaveRequested;

        public event Action<DtmfPresetRequest>? PreviewRequested;

        public event Action<DtmfPresetRequest>? SendRequested;

        public event PropertyChangedEventHandler? PropertyChanged;

        public void AddPreset()
        {
            var preset = new DtmfPresetItem
            {
                Id = Guid.NewGuid().ToString("N"),
                DisplayName = "New DTMF Preset",
                TargetResourceKey = SelectedTarget?.Key ?? string.Empty,
            };
            preset.Steps.Add(new DtmfPresetStepItem());
            Presets.Add(preset);
            SelectedPreset = preset;
        }

        public void AddDigit()
        {
            EnsureSelectedPreset();
            SelectedPreset?.Steps.Add(new DtmfPresetStepItem());
        }

        public void AddHold()
        {
            EnsureSelectedPreset();
            SelectedPreset?.Steps.Add(new DtmfPresetStepItem
            {
                Kind = "hold",
                Digit = string.Empty,
                DurationSeconds = 0.75,
            });
        }

        public void DeleteStep(DtmfPresetStepItem? step)
        {
            if (SelectedPreset is not null && step is not null)
                SelectedPreset.Steps.Remove(step);
        }

        public void MoveStep(DtmfPresetStepItem? step, int direction)
        {
            if (SelectedPreset is null || step is null)
                return;

            int oldIndex = SelectedPreset.Steps.IndexOf(step);
            int newIndex = oldIndex + direction;
            if (oldIndex < 0 || newIndex < 0 || newIndex >= SelectedPreset.Steps.Count)
                return;

            SelectedPreset.Steps.Move(oldIndex, newIndex);
        }

        public void DeleteSelected()
        {
            if (SelectedPreset is null)
                return;

            int removedIndex = Presets.IndexOf(SelectedPreset);
            Presets.Remove(SelectedPreset);
            SelectedPreset = Presets.Count == 0
                ? null
                : Presets[Math.Min(removedIndex, Presets.Count - 1)];
        }

        public void Commit()
        {
            SaveRequested?.Invoke(Presets
                .Select(ToConfig)
                .Where(config => config.Steps.Count > 0)
                .ToList());
        }

        public void Preview()
        {
            if (BuildRequest() is { } request)
                PreviewRequested?.Invoke(request);
        }

        public void Send()
        {
            if (BuildRequest() is { } request)
                SendRequested?.Invoke(request);
        }

        private DtmfPresetRequest? BuildRequest()
        {
            if (SelectedPreset is null || SelectedTarget is null)
                return null;

            var config = ToConfig(SelectedPreset);
            if (config.Steps.Count == 0)
                return null;

            byte[] pcm = TonePcmSequencer.BuildDtmfPresetPcm(config.Steps);
            if (pcm.Length == 0)
                return null;

            return new DtmfPresetRequest(
                config.Id,
                config.TargetResourceKey,
                pcm);
        }

        private void EnsureSelectedPreset()
        {
            if (SelectedPreset is null)
                AddPreset();
        }

        private DtmfPresetTarget? ResolveTarget(DtmfPresetItem? preset)
        {
            if (preset is not null && !string.IsNullOrWhiteSpace(preset.TargetResourceKey))
            {
                DtmfPresetTarget? saved = Targets.FirstOrDefault(target =>
                    string.Equals(target.Key, preset.TargetResourceKey, StringComparison.OrdinalIgnoreCase));
                if (saved is not null)
                    return saved;
            }

            return selectedTarget ?? Targets.FirstOrDefault();
        }

        private static DtmfPresetItem Normalize(UserSettingsDtmfPresetConfig config)
        {
            var item = new DtmfPresetItem
            {
                Id = string.IsNullOrWhiteSpace(config.Id)
                    ? Guid.NewGuid().ToString("N")
                    : config.Id,
                DisplayName = string.IsNullOrWhiteSpace(config.DisplayName)
                    ? "DTMF Preset"
                    : config.DisplayName.Trim(),
                TargetResourceKey = config.TargetResourceKey?.Trim() ?? string.Empty,
            };

            foreach (UserSettingsDtmfPresetStep? step in config.Steps ?? new List<UserSettingsDtmfPresetStep>())
            {
                if (step is null)
                    continue;

                bool hold = IsHold(step.Kind);
                item.Steps.Add(new DtmfPresetStepItem
                {
                    Kind = hold ? "hold" : "digit",
                    Digit = hold ? string.Empty : NormalizeDigit(step.Digit),
                    DurationSeconds = step.DurationSeconds,
                });
            }

            return item;
        }

        private static UserSettingsDtmfPresetConfig ToConfig(DtmfPresetItem item)
        {
            var config = new UserSettingsDtmfPresetConfig
            {
                Id = string.IsNullOrWhiteSpace(item.Id)
                    ? Guid.NewGuid().ToString("N")
                    : item.Id,
                DisplayName = string.IsNullOrWhiteSpace(item.DisplayName)
                    ? "DTMF Preset"
                    : item.DisplayName.Trim(),
                TargetResourceKey = item.TargetResourceKey?.Trim() ?? string.Empty,
            };

            foreach (DtmfPresetStepItem step in item.Steps)
            {
                if (step is null)
                    continue;

                bool hold = IsHold(step.Kind);
                config.Steps.Add(new UserSettingsDtmfPresetStep
                {
                    Kind = hold ? "hold" : "digit",
                    Digit = hold ? string.Empty : NormalizeDigit(step.Digit),
                    DurationSeconds = Math.Clamp(
                        step.DurationSeconds,
                        MinimumDurationSeconds,
                        MaximumDurationSeconds),
                });
            }

            return config;
        }

        private static string NormalizeDigit(string? digit)
        {
            string normalized = (digit ?? string.Empty).Trim().ToUpperInvariant();
            if (normalized.Length > 1)
                normalized = normalized.Substring(0, 1);

            return normalized.Length == 1 && ValidDigits.Contains(normalized)
                ? normalized
                : "1";
        }

        private static bool IsHold(string? kind)
            => string.Equals(kind, "hold", StringComparison.OrdinalIgnoreCase);

        public sealed class DtmfPresetItem : INotifyPropertyChanged
        {
            private string displayName = string.Empty;
            private string targetResourceKey = string.Empty;

            public string Id { get; internal set; } = Guid.NewGuid().ToString("N");

            public ObservableCollection<DtmfPresetStepItem> Steps { get; } = new();

            public string DisplayName
            {
                get => displayName;
                set
                {
                    string normalized = value?.Trim() ?? string.Empty;
                    if (displayName == normalized)
                        return;
                    displayName = normalized;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
                }
            }

            public string TargetResourceKey
            {
                get => targetResourceKey;
                set
                {
                    string normalized = value?.Trim() ?? string.Empty;
                    if (targetResourceKey == normalized)
                        return;
                    targetResourceKey = normalized;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TargetResourceKey)));
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
        }

        public sealed class DtmfPresetStepItem : INotifyPropertyChanged
        {
            private string kind = "digit";
            private string digit = "1";
            private double durationSeconds = MinimumDurationSeconds;

            public string Kind
            {
                get => kind;
                set
                {
                    string normalized = IsHold(value) ? "hold" : "digit";
                    if (kind == normalized)
                        return;
                    kind = normalized;
                    if (IsHold(kind))
                        Digit = string.Empty;
                    else if (string.IsNullOrWhiteSpace(Digit))
                        Digit = "1";
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Kind)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DigitDisplay)));
                }
            }

            public string Digit
            {
                get => digit;
                set
                {
                    string normalized = NormalizeDigit(value);
                    if (digit == normalized)
                        return;
                    digit = normalized;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Digit)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DigitDisplay)));
                }
            }

            public string DigitDisplay
            {
                get => IsHold(Kind) ? "Hold" : Digit;
                set
                {
                    if (!IsHold(Kind))
                        Digit = value;
                }
            }

            public double DurationSeconds
            {
                get => durationSeconds;
                set
                {
                    if (Math.Abs(durationSeconds - value) < 0.0001)
                        return;
                    durationSeconds = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DurationSeconds)));
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
        }
    }
}
