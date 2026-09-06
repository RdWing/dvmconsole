// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Settings;
using DvmConsole.Audio;
using DvmConsole.Application;
using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

public sealed class AlertToneViewModel : IAlertToneViewModel
{
    public AlertToneViewModel(AlertToneSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        Name = setting.Name;
        AssetId = setting.AssetId;
        FileName = string.IsNullOrWhiteSpace(setting.FileName)
            ? Path.GetFileName(setting.FilePath)
            : Path.GetFileName(setting.FileName);
        FilePath = setting.FilePath;
    }

    public string Name { get; }
    public string? AssetId { get; private set; }
    public string FilePath { get; private set; }
    public string FileName { get; }
    public string DisplayText => $"{Name} — {FileName}";
    public string StorageText => Guid.TryParse(AssetId, out _)
        ? $"Managed asset · {FileName}"
        : FilePath;
    public bool IsAvailable => Guid.TryParse(AssetId, out _) || File.Exists(FilePath);

    internal void SetManagedAsset(AssetId id)
    {
        AssetId = id.ToString();
        FilePath = string.Empty;
    }

    public AlertToneSetting ToSetting()
        => new()
        {
            Name = Name,
            AssetId = AssetId,
            FileName = FileName,
            FilePath = FilePath
        };
}

public sealed class BuiltInAlertToneViewModel : System.ComponentModel.INotifyPropertyChanged
{
    public BuiltInAlertToneViewModel(LegacyAlertTone tone)
    {
        Tone = tone;

        defaultDescription = tone switch
        {
            LegacyAlertTone.Alert1 => "Generate 1 kHz for 3 sec",
            LegacyAlertTone.Alert2 => "Generate alternating 1.5 kHz / 800 Hz tones for 3.36 sec",
            LegacyAlertTone.Alert3 => "Generate eight 1 kHz pulses over 3.6 sec",
            _ => throw new ArgumentOutOfRangeException(nameof(tone))
        };
    }

    public LegacyAlertTone Tone { get; }
    private readonly string defaultDescription;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public string? AssignedPresetName { get; private set; }
    public bool IsCustomAudio { get; private set; }
    internal string? AssignedAssetId { get; private set; }
    internal string? AssignedFilePath { get; private set; }
    public string Name => AssignedPresetName ?? $"ALERT {(int)Tone}";
    public string DisplayName
    {
        get
        {
            if (AssignedPresetName is null)
                return Name;
            string firstWord = AssignedPresetName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? string.Empty;
            return (firstWord.Length > 7 ? firstWord[..7] : firstWord).ToUpperInvariant();
        }
    }
    public string Description => AssignedPresetName is null
        ? $"{defaultDescription}. Right-click to assign a saved pattern."
        : $"Send {AssignedPresetName} on ALERT channels. Right-click to change.";

    public void Assign(ToolbarToneAssignmentSetting? assignment)
    {
        AssignedPresetName = assignment?.PresetName;
        IsCustomAudio = assignment?.IsCustomAudio ?? false;
        AssignedAssetId = assignment?.AssetId;
        AssignedFilePath = assignment?.FilePath;
        PropertyChanged?.Invoke(this, new(nameof(Name)));
        PropertyChanged?.Invoke(this, new(nameof(DisplayName)));
        PropertyChanged?.Invoke(this, new(nameof(Description)));
    }

    public short[] GenerateSamples()
        => LegacyAlertToneGenerator.Generate(Tone);

    public GeneratedToneSequence CreateSequence()
        => LegacyAlertToneGenerator.CreateSequence(Tone);
}
