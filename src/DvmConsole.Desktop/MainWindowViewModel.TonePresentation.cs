// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel : IToneSettingsViewModel
{
    string IToneSettingsViewModel.AlertTargetSummary => toneWorkspace.AlertTargetSummary;
    string IToneSettingsViewModel.AlertTargetDetails => toneWorkspace.AlertTargetDetails;
    string IToneSettingsViewModel.PageTargetSummary => toneWorkspace.PageTargetSummary;
    string IToneSettingsViewModel.PageTargetDetails => toneWorkspace.PageTargetDetails;

    System.Collections.IEnumerable IToneSettingsViewModel.DtmfPresets => DtmfPresets;
    string IToneSettingsViewModel.DtmfPresetFilterText
    {
        get => toneWorkspace.DtmfPresetFilterText;
        set => toneWorkspace.DtmfPresetFilterText = value;
    }
    bool IToneSettingsViewModel.IsDtmfPresetFilterVisible
        => toneWorkspace.IsDtmfPresetFilterVisible;
    System.Collections.IEnumerable IToneSettingsViewModel.FilteredDtmfPresets
        => toneWorkspace.FilteredDtmfPresets;
    System.Collections.IEnumerable IToneSettingsViewModel.ToneSequenceSteps => ToneSequenceSteps;
    System.Collections.IEnumerable IToneSettingsViewModel.TonePresets => TonePresets;
    string IToneSettingsViewModel.TonePresetFilterText
    {
        get => toneWorkspace.TonePresetFilterText;
        set => toneWorkspace.TonePresetFilterText = value;
    }
    bool IToneSettingsViewModel.IsTonePresetFilterVisible
        => toneWorkspace.IsTonePresetFilterVisible;
    System.Collections.IEnumerable IToneSettingsViewModel.FilteredTonePresets
        => toneWorkspace.FilteredTonePresets;
    System.Collections.IEnumerable IToneSettingsViewModel.AlertTones => AlertTones;
    string IToneSettingsViewModel.AlertToneFilterText
    {
        get => toneWorkspace.AlertToneFilterText;
        set => toneWorkspace.AlertToneFilterText = value;
    }
    bool IToneSettingsViewModel.IsAlertToneFilterVisible
        => toneWorkspace.IsAlertToneFilterVisible;
    System.Collections.IEnumerable IToneSettingsViewModel.FilteredAlertTones
        => toneWorkspace.FilteredAlertTones;

    private void RefreshToneTargets()
        => toneWorkspace.UpdateTargets(
            ToneTargetSummary.Create("ALERT", ResolveGeneratedToneChannels()),
            ToneTargetSummary.Create("PAGE", ResolvePageToneChannels()));

    private void HandleToneTargetChannelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(ChannelViewModel.IsAlertSelected) or nameof(ChannelViewModel.IsPageSelected)))
            return;
        toneTargetRefresh.Schedule();
    }
}
