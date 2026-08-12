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
    /// A selectable resource for a generated tone preset.
    /// </summary>
    public sealed class TonePresetTarget
    {
        public TonePresetTarget(string key, string displayName)
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
    /// Detached generated PCM request emitted by the tone preset manager.
    /// The shell owns playback or transmission of the request.
    /// </summary>
    public sealed class TonePresetRequest
    {
        public TonePresetRequest(
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
    /// Headless managed state for generated tone preset editing. It owns row
    /// normalization and detached request payloads; persistence, playback, and
    /// transmission remain shell-owned.
    /// </summary>
    public sealed class TonePresetManagerViewModel
    {
        public const double MinimumFrequencyHz = 1;
        public const double MaximumFrequencyHz = 4000;
        public const double MinimumDurationSeconds = 0.25;
        public const double MaximumDurationSeconds = 10;

        private TonePresetItem? selectedPreset;
        private TonePresetTarget? selectedTarget;

        public TonePresetManagerViewModel(
            IEnumerable<UserSettingsTonePresetConfig>? configs,
            IEnumerable<TonePresetTarget>? targets)
        {
            Targets = (targets ?? Enumerable.Empty<TonePresetTarget>())
                .Where(target => target is not null && !string.IsNullOrWhiteSpace(target.Key))
                .GroupBy(target => target.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            Presets = new ObservableCollection<TonePresetItem>(
                (configs ?? Enumerable.Empty<UserSettingsTonePresetConfig>())
                    .Where(config => config is not null)
                    .Select(Normalize)
                    .OrderBy(preset => preset.DisplayName, StringComparer.OrdinalIgnoreCase));

            if (Presets.Count > 0)
                SelectedPreset = Presets[0];
            else if (Targets.Count > 0)
                SelectedTarget = Targets[0];
        }

        public ObservableCollection<TonePresetItem> Presets { get; }

        public IReadOnlyList<TonePresetTarget> Targets { get; }

        public IReadOnlyList<string> StepKinds { get; } = new[] { "tone", "hold" };

        public TonePresetItem? SelectedPreset
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

        public TonePresetTarget? SelectedTarget
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

        public event Action<IReadOnlyList<UserSettingsTonePresetConfig>>? SaveRequested;

        public event Action<TonePresetRequest>? PreviewRequested;

        public event Action<TonePresetRequest>? SendRequested;

        public event PropertyChangedEventHandler? PropertyChanged;

        public void AddPreset()
        {
            var preset = new TonePresetItem
            {
                Id = Guid.NewGuid().ToString("N"),
                DisplayName = "New Tone Preset",
                TargetResourceKey = SelectedTarget?.Key ?? string.Empty,
            };
            preset.Steps.Add(new TonePresetStepItem());
            Presets.Add(preset);
            SelectedPreset = preset;
        }

        public void AddTone()
        {
            EnsureSelectedPreset();
            SelectedPreset?.Steps.Add(new TonePresetStepItem());
        }

        public void AddHold()
        {
            EnsureSelectedPreset();
            SelectedPreset?.Steps.Add(new TonePresetStepItem
            {
                Kind = "hold",
                FrequencyHz = 0,
                DurationSeconds = 0.75,
            });
        }

        public void DeleteStep(TonePresetStepItem? step)
        {
            if (SelectedPreset is not null && step is not null)
                SelectedPreset.Steps.Remove(step);
        }

        public void MoveStep(TonePresetStepItem? step, int direction)
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

        private TonePresetRequest? BuildRequest()
        {
            if (SelectedPreset is null || SelectedTarget is null)
                return null;

            var config = ToConfig(SelectedPreset);
            if (config.Steps.Count == 0)
                return null;

            byte[] pcm = TonePcmSequencer.BuildTonePresetPcm(config.Steps);
            if (pcm.Length == 0)
                return null;

            return new TonePresetRequest(
                config.Id,
                config.TargetResourceKey,
                pcm);
        }

        private void EnsureSelectedPreset()
        {
            if (SelectedPreset is null)
                AddPreset();
        }

        private TonePresetTarget? ResolveTarget(TonePresetItem? preset)
        {
            if (preset is not null && !string.IsNullOrWhiteSpace(preset.TargetResourceKey))
            {
                TonePresetTarget? saved = Targets.FirstOrDefault(target =>
                    string.Equals(target.Key, preset.TargetResourceKey, StringComparison.OrdinalIgnoreCase));
                if (saved is not null)
                    return saved;
            }

            return selectedTarget ?? Targets.FirstOrDefault();
        }

        private static TonePresetItem Normalize(UserSettingsTonePresetConfig config)
        {
            var item = new TonePresetItem
            {
                Id = string.IsNullOrWhiteSpace(config.Id)
                    ? Guid.NewGuid().ToString("N")
                    : config.Id,
                DisplayName = string.IsNullOrWhiteSpace(config.DisplayName)
                    ? "Tone Preset"
                    : config.DisplayName.Trim(),
                TargetResourceKey = config.TargetResourceKey?.Trim() ?? string.Empty,
            };

            foreach (UserSettingsTonePresetStep? step in config.Steps ?? new List<UserSettingsTonePresetStep>())
            {
                if (step is null)
                    continue;

                item.Steps.Add(new TonePresetStepItem
                {
                    Kind = IsHold(step.Kind) ? "hold" : "tone",
                    FrequencyHz = step.FrequencyHz,
                    DurationSeconds = step.DurationSeconds,
                });
            }

            return item;
        }

        private static UserSettingsTonePresetConfig ToConfig(TonePresetItem item)
        {
            var config = new UserSettingsTonePresetConfig
            {
                Id = string.IsNullOrWhiteSpace(item.Id)
                    ? Guid.NewGuid().ToString("N")
                    : item.Id,
                DisplayName = string.IsNullOrWhiteSpace(item.DisplayName)
                    ? "Tone Preset"
                    : item.DisplayName.Trim(),
                TargetResourceKey = item.TargetResourceKey?.Trim() ?? string.Empty,
            };

            foreach (TonePresetStepItem step in item.Steps)
            {
                if (step is null)
                    continue;

                bool hold = IsHold(step.Kind);
                config.Steps.Add(new UserSettingsTonePresetStep
                {
                    Kind = hold ? "hold" : "tone",
                    FrequencyHz = hold
                        ? 0
                        : Math.Clamp(step.FrequencyHz, MinimumFrequencyHz, MaximumFrequencyHz),
                    DurationSeconds = Math.Clamp(
                        step.DurationSeconds,
                        MinimumDurationSeconds,
                        MaximumDurationSeconds),
                });
            }

            return config;
        }

        private static bool IsHold(string? kind)
            => string.Equals(kind, "hold", StringComparison.OrdinalIgnoreCase);

        public sealed class TonePresetItem : INotifyPropertyChanged
        {
            private string displayName = string.Empty;
            private string targetResourceKey = string.Empty;

            public string Id { get; internal set; } = Guid.NewGuid().ToString("N");

            public ObservableCollection<TonePresetStepItem> Steps { get; } = new();

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

        public sealed class TonePresetStepItem : INotifyPropertyChanged
        {
            private string kind = "tone";
            private double frequencyHz = 1000;
            private double durationSeconds = 1;

            public string Kind
            {
                get => kind;
                set
                {
                    string normalized = IsHold(value) ? "hold" : "tone";
                    if (kind == normalized)
                        return;
                    kind = normalized;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Kind)));
                }
            }

            public double FrequencyHz
            {
                get => frequencyHz;
                set
                {
                    if (Math.Abs(frequencyHz - value) < 0.0001)
                        return;
                    frequencyHz = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FrequencyHz)));
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
