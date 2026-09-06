// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DvmConsole.Presentation;

public sealed partial class ToneSettingsView : UserControl
{
    public ToneSettingsView()
    {
        InitializeComponent();
        ResponsiveSettingsDensity.Attach(this);
    }

    public event EventHandler<DtmfPresetEventArgs>? UseDtmfPresetRequested;
    public event EventHandler<DtmfPresetEventArgs>? SendDtmfPresetRequested;
    public event EventHandler<DtmfPresetEventArgs>? DeleteDtmfPresetRequested;
    public event EventHandler<TonePresetEventArgs>? UseTonePresetRequested;
    public event EventHandler<TonePresetEventArgs>? SendTonePresetRequested;
    public event EventHandler<TonePresetEventArgs>? DeleteTonePresetRequested;
    public event EventHandler? SendQuickCallRequested;
    public event EventHandler? AddToneStepRequested;
    public event EventHandler? AddSilenceStepRequested;
    public event EventHandler<ToneSequenceStepEventArgs>? RemoveToneStepRequested;
    public event EventHandler<ToneSequenceStepEventArgs>? MoveToneStepUpRequested;
    public event EventHandler<ToneSequenceStepEventArgs>? MoveToneStepDownRequested;
    public event EventHandler? ImportAlertToneRequested;
    public event EventHandler<AlertToneEventArgs>? SendAlertToneRequested;
    public event EventHandler<AlertToneEventArgs>? DeleteAlertToneRequested;

    private void HandleSectionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string sectionName })
            this.FindControl<Control>(sectionName)?.BringIntoView();
    }

    private void HandlePresetRowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid row || row.Children.Count != 2)
            return;

        // Preserve room for the preset name beside the row actions.
        // Change layout only at the breakpoint to avoid repeated layout work.
        bool stackActions = e.NewSize.Width < 560;
        if (stackActions == (row.ColumnDefinitions.Count == 1))
            return;
        row.ColumnDefinitions = new ColumnDefinitions(stackActions ? "*" : "*,Auto");
        row.RowDefinitions = new RowDefinitions(stackActions ? "Auto,Auto" : "Auto");
        Grid.SetColumn(row.Children[1], stackActions ? 0 : 1);
        Grid.SetRow(row.Children[1], stackActions ? 1 : 0);
    }

    private void HandleUseDtmfPresetClick(object? sender, RoutedEventArgs e)
        => PublishDtmfPreset(sender, UseDtmfPresetRequested);
    private void HandleSendDtmfPresetClick(object? sender, RoutedEventArgs e)
        => PublishDtmfPreset(sender, SendDtmfPresetRequested);
    private void HandleDeleteDtmfPresetClick(object? sender, RoutedEventArgs e)
        => PublishDtmfPreset(sender, DeleteDtmfPresetRequested);
    private void HandleUseTonePresetClick(object? sender, RoutedEventArgs e)
        => PublishTonePreset(sender, UseTonePresetRequested);
    private void HandleSendTonePresetClick(object? sender, RoutedEventArgs e)
        => PublishTonePreset(sender, SendTonePresetRequested);
    private void HandleDeleteTonePresetClick(object? sender, RoutedEventArgs e)
        => PublishTonePreset(sender, DeleteTonePresetRequested);
    private void HandleSendQuickCallClick(object? sender, RoutedEventArgs e)
        => SendQuickCallRequested?.Invoke(this, EventArgs.Empty);
    private void HandleAddToneStepClick(object? sender, RoutedEventArgs e)
        => AddToneStepRequested?.Invoke(this, EventArgs.Empty);
    private void HandleAddSilenceStepClick(object? sender, RoutedEventArgs e)
        => AddSilenceStepRequested?.Invoke(this, EventArgs.Empty);
    private void HandleRemoveToneStepClick(object? sender, RoutedEventArgs e)
        => PublishToneStep(sender, RemoveToneStepRequested);
    private void HandleMoveToneStepUpClick(object? sender, RoutedEventArgs e)
        => PublishToneStep(sender, MoveToneStepUpRequested);
    private void HandleMoveToneStepDownClick(object? sender, RoutedEventArgs e)
        => PublishToneStep(sender, MoveToneStepDownRequested);
    private void HandleImportAlertToneClick(object? sender, RoutedEventArgs e)
        => ImportAlertToneRequested?.Invoke(this, EventArgs.Empty);
    private void HandleSendAlertToneClick(object? sender, RoutedEventArgs e)
        => PublishAlertTone(sender, SendAlertToneRequested);
    private void HandleDeleteAlertToneClick(object? sender, RoutedEventArgs e)
        => PublishAlertTone(sender, DeleteAlertToneRequested);

    private void PublishDtmfPreset(object? sender, EventHandler<DtmfPresetEventArgs>? handler)
    {
        if (sender is Button { Tag: IDtmfPresetViewModel preset })
            handler?.Invoke(this, new DtmfPresetEventArgs(preset));
    }

    private void PublishTonePreset(object? sender, EventHandler<TonePresetEventArgs>? handler)
    {
        if (sender is Button { Tag: ITonePresetViewModel preset })
            handler?.Invoke(this, new TonePresetEventArgs(preset));
    }

    private void PublishToneStep(object? sender, EventHandler<ToneSequenceStepEventArgs>? handler)
    {
        if (sender is Button { Tag: IToneSequenceStepViewModel step })
            handler?.Invoke(this, new ToneSequenceStepEventArgs(step));
    }

    private void PublishAlertTone(object? sender, EventHandler<AlertToneEventArgs>? handler)
    {
        if (sender is Button { Tag: IAlertToneViewModel tone })
            handler?.Invoke(this, new AlertToneEventArgs(tone));
    }
}
