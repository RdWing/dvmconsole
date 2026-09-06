// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Audio;
using DvmConsole.Core.Settings;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace DvmConsole.Desktop;

internal sealed class ToneWorkspaceViewModel : INotifyPropertyChanged
{
    private readonly ObservableCollection<DtmfPresetViewModel> dtmfPresets = [];
    private readonly ObservableCollection<TonePresetViewModel> tonePresets = [];
    private readonly ObservableCollection<ToneSequenceStepViewModel> toneSequenceSteps = [];
    private readonly ObservableCollection<AlertToneViewModel> alertTones = [];
    private readonly ObservableCollection<BuiltInAlertToneViewModel> builtInAlertTones = [];
    private readonly FilteredPresetCollection<DtmfPresetViewModel> filteredDtmfPresets;
    private readonly FilteredPresetCollection<TonePresetViewModel> filteredTonePresets;
    private readonly FilteredPresetCollection<AlertToneViewModel> filteredAlertTones;
    private string dtmfDigits;
    private string toneFrequencyText;
    private string toneDurationText;
    private string quickCallToneAText;
    private string quickCallToneBText;
    private string dtmfPresetName = string.Empty;
    private string tonePresetName = string.Empty;
    private string alertToneNameText = string.Empty;
    private ToneTargetSummary alertTargets = ToneTargetSummary.Create("ALERT", []);
    private ToneTargetSummary pageTargets = ToneTargetSummary.Create("PAGE", []);

    public ToneWorkspaceViewModel(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        dtmfDigits = settings.LastDtmfDigits;
        toneFrequencyText = settings.ToneFrequencyHz.ToString("0.###", CultureInfo.InvariantCulture);
        toneDurationText = settings.ToneDurationSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        quickCallToneAText = settings.QuickCallToneAFrequencyHz.ToString("0.###", CultureInfo.InvariantCulture);
        quickCallToneBText = settings.QuickCallToneBFrequencyHz.ToString("0.###", CultureInfo.InvariantCulture);

        toneSequenceSteps.Add(new ToneSequenceStepViewModel(
            settings.ToneFrequencyHz,
            settings.ToneDurationSeconds));
        foreach (DtmfPresetSetting preset in settings.DtmfPresets)
            dtmfPresets.Add(new DtmfPresetViewModel(preset));
        foreach (TonePresetSetting preset in settings.TonePresets)
            tonePresets.Add(new TonePresetViewModel(preset));
        foreach (AlertToneSetting tone in settings.AlertTones)
            alertTones.Add(new AlertToneViewModel(tone));
        builtInAlertTones.Add(new BuiltInAlertToneViewModel(LegacyAlertTone.Alert1));
        builtInAlertTones.Add(new BuiltInAlertToneViewModel(LegacyAlertTone.Alert2));
        builtInAlertTones.Add(new BuiltInAlertToneViewModel(LegacyAlertTone.Alert3));
        foreach (BuiltInAlertToneViewModel tone in builtInAlertTones)
            tone.Assign(settings.ToolbarToneAssignments.GetValueOrDefault((int)tone.Tone));

        filteredDtmfPresets = new FilteredPresetCollection<DtmfPresetViewModel>(
            dtmfPresets,
            preset => preset.DisplayText);
        filteredTonePresets = new FilteredPresetCollection<TonePresetViewModel>(
            tonePresets,
            preset => preset.DisplayText);
        filteredAlertTones = new FilteredPresetCollection<AlertToneViewModel>(
            alertTones,
            tone => tone.DisplayText);
        filteredDtmfPresets.StateChanged += (_, _) => NotifyPropertiesChanged(
            nameof(FilteredDtmfPresets),
            nameof(IsDtmfPresetFilterVisible));
        filteredTonePresets.StateChanged += (_, _) => NotifyPropertiesChanged(
            nameof(FilteredTonePresets),
            nameof(IsTonePresetFilterVisible));
        filteredAlertTones.StateChanged += (_, _) => NotifyPropertiesChanged(
            nameof(FilteredAlertTones),
            nameof(IsAlertToneFilterVisible));
        DtmfPresets = new ReadOnlyObservableCollection<DtmfPresetViewModel>(dtmfPresets);
        TonePresets = new ReadOnlyObservableCollection<TonePresetViewModel>(tonePresets);
        ToneSequenceSteps = new ReadOnlyObservableCollection<ToneSequenceStepViewModel>(toneSequenceSteps);
        AlertTones = new ReadOnlyObservableCollection<AlertToneViewModel>(alertTones);
        BuiltInAlertTones = new ReadOnlyObservableCollection<BuiltInAlertToneViewModel>(builtInAlertTones);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string AlertTargetSummary => alertTargets.Text;
    public string AlertTargetDetails => alertTargets.Details;
    public string PageTargetSummary => pageTargets.Text;
    public string PageTargetDetails => pageTargets.Details;

    internal void UpdateTargets(ToneTargetSummary alerts, ToneTargetSummary pages)
    {
        if (alertTargets != alerts)
        {
            alertTargets = alerts;
            NotifyPropertiesChanged(nameof(AlertTargetSummary), nameof(AlertTargetDetails));
        }
        if (pageTargets != pages)
        {
            pageTargets = pages;
            NotifyPropertiesChanged(nameof(PageTargetSummary), nameof(PageTargetDetails));
        }
    }

    internal ObservableCollection<DtmfPresetViewModel> MutableDtmfPresets => dtmfPresets;
    internal ObservableCollection<TonePresetViewModel> MutableTonePresets => tonePresets;
    internal ObservableCollection<ToneSequenceStepViewModel> MutableToneSequenceSteps => toneSequenceSteps;
    internal ObservableCollection<AlertToneViewModel> MutableAlertTones => alertTones;

    public ReadOnlyObservableCollection<DtmfPresetViewModel> DtmfPresets { get; }
    public ReadOnlyObservableCollection<TonePresetViewModel> TonePresets { get; }
    public ReadOnlyObservableCollection<ToneSequenceStepViewModel> ToneSequenceSteps { get; }
    public ReadOnlyObservableCollection<AlertToneViewModel> AlertTones { get; }
    public ReadOnlyObservableCollection<BuiltInAlertToneViewModel> BuiltInAlertTones { get; }
    public ReadOnlyObservableCollection<DtmfPresetViewModel> FilteredDtmfPresets
        => filteredDtmfPresets.Items;
    public ReadOnlyObservableCollection<TonePresetViewModel> FilteredTonePresets
        => filteredTonePresets.Items;
    public ReadOnlyObservableCollection<AlertToneViewModel> FilteredAlertTones
        => filteredAlertTones.Items;
    public bool IsDtmfPresetFilterVisible => filteredDtmfPresets.IsFilterVisible;
    public bool IsTonePresetFilterVisible => filteredTonePresets.IsFilterVisible;
    public bool IsAlertToneFilterVisible => filteredAlertTones.IsFilterVisible;

    public string DtmfPresetFilterText
    {
        get => filteredDtmfPresets.FilterText;
        set => filteredDtmfPresets.FilterText = value;
    }

    public string TonePresetFilterText
    {
        get => filteredTonePresets.FilterText;
        set => filteredTonePresets.FilterText = value;
    }

    public string AlertToneFilterText
    {
        get => filteredAlertTones.FilterText;
        set => filteredAlertTones.FilterText = value;
    }

    public string DtmfDigits
    {
        get => dtmfDigits;
        set => SetField(ref dtmfDigits, value ?? string.Empty);
    }

    public string ToneFrequencyText
    {
        get => toneFrequencyText;
        set => SetField(ref toneFrequencyText, value ?? string.Empty);
    }

    public string ToneDurationText
    {
        get => toneDurationText;
        set => SetField(ref toneDurationText, value ?? string.Empty);
    }

    public string DtmfPresetName
    {
        get => dtmfPresetName;
        set => SetField(ref dtmfPresetName, value ?? string.Empty);
    }

    public string TonePresetName
    {
        get => tonePresetName;
        set => SetField(ref tonePresetName, value ?? string.Empty);
    }

    public string QuickCallToneAText
    {
        get => quickCallToneAText;
        set => SetField(ref quickCallToneAText, value ?? string.Empty);
    }

    public string QuickCallToneBText
    {
        get => quickCallToneBText;
        set => SetField(ref quickCallToneBText, value ?? string.Empty);
    }

    public string AlertToneNameText
    {
        get => alertToneNameText;
        set => SetField(ref alertToneNameText, value ?? string.Empty);
    }

    private void SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void NotifyPropertiesChanged(string firstProperty, string secondProperty)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(firstProperty));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(secondProperty));
    }
}
