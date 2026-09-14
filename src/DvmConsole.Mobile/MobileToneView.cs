// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System.ComponentModel;
using DvmConsole.Audio;
using DvmConsole.Presentation;

namespace DvmConsole.Mobile;

internal sealed class MobileToneView : UserControl, IAsyncDisposable
{
    public event EventHandler? SettingsRequested;
    private MobileToneViewModel? model;
    private readonly ToneSettingsView editor = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button undo = new() { Content = "Undo", MinHeight = 44, IsEnabled = false };
    private readonly Button save = new() { Content = "Retry save", MinHeight = 44, IsVisible = false };
    private readonly ComboBox alertAssignment = new() { MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
    private bool updatingAssignment;
    private sealed record AlertAssignment(string Name, int? BuiltIn = null, string? AssetId = null)
    {
        public override string ToString() => string.IsNullOrEmpty(Name) ? "Unassigned" : Name;
    }
    private readonly ToggleSwitch monitor = new() { Content = "Monitor tones locally", MinHeight = 44 };
    private readonly WrapPanel builtIns = new() { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };

    public MobileToneView()
    {
        var picker = new DvmConsole.Host.TonePatternFilePicker(this);
        editor.PickImportDocumentAsync = picker.ImportAsync;
        editor.SaveExportDocumentAsync = picker.ExportAsync;
        status.IsVisible = false;
        status.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBlock.TextProperty)
                status.IsVisible = !string.IsNullOrWhiteSpace(status.Text);
        };
        var back = new Button { Content = "‹ Settings", MinHeight = 44 };
        var stop = new Button { Content = "Cancel tones", MinHeight = 44 };
        var header = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(12) };
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
        actions.Children.Add(back); actions.Children.Add(stop); actions.Children.Add(undo); actions.Children.Add(save); actions.Children.Add(editor.CreatePatternExchangeButtons());
        header.Children.Add(status);
        foreach (LegacyAlertTone tone in Enum.GetValues<LegacyAlertTone>())
        {
            var button = new Button { Content = tone.ToString(), MinHeight = 44 };
            button.Click += async (_, _) => { if (model is not null) await model.SendBuiltInAsync(tone); };
            builtIns.Children.Add(button);
        }
        var assignment = new StackPanel { Spacing = 6 };
        assignment.Children.Add(new TextBlock { Text = "Main ALERT pattern" }.WithScaledFontSize(16));
        assignment.Children.Add(alertAssignment);
        header.Children.Add(MobileSettingsSurface.Group([assignment, monitor], paddedRows: true));
        header.Children.Add(MobileSettingsSurface.Caption("Quick alerts"));
        header.Children.Add(builtIns);
        AutomationProperties.SetName(alertAssignment, "Tone pattern assigned to the main ALERT button");
        alertAssignment.SelectionChanged += async (_, _) =>
        {
            if (!updatingAssignment && model is not null && alertAssignment.SelectedItem is AlertAssignment choice)
                await model.AssignMainAlertAsync(choice.Name, choice.BuiltIn, choice.AssetId);
        };
        actions.Margin = new Avalonia.Thickness(12, 12, 12, 0);
        // Options and the selected tone family share the editor's single scroll owner.
        // The toolbar remains available even in landscape or with large text.
        editor.UseFocusedNavigation(header);
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 8 };
        body.Children.Add(actions);
        Grid.SetRow(editor, 1); body.Children.Add(editor); Content = body;
        monitor.IsCheckedChanged += async (_, _) =>
        {
            if (model is not null && monitor.IsChecked != model.LocalMonitorEnabled)
                await model.SetLocalMonitorAsync(monitor.IsChecked == true);
        };
        back.Click += async (_, _) =>
        {
            if (model is not null) { await model.CancelAsync(); await model.RetrySaveAsync(); if (model.HasUnsavedChanges) return; }
            SettingsRequested?.Invoke(this, EventArgs.Empty);
        };
        stop.Click += async (_, _) => { if (model is not null) await model.CancelAsync(); };
        undo.Click += async (_, _) => { if (model is not null) await model.UndoAsync(); };
        save.Click += async (_, _) => { if (model is not null) await model.RetrySaveAsync(); };
        editor.UseDtmfPresetRequested += async (_, e) => { if (model is not null) await model.EditAsync(() => model.Editor.LoadDtmfPreset((DtmfPresetViewModel)e.Preset)); };
        editor.SendDtmfPresetRequested += async (_, e) => { if (model is not null) await model.SendDtmfAsync((DtmfPresetViewModel)e.Preset); };
        editor.DeleteDtmfPresetRequested += async (_, e) => { if (model is not null) await model.EditAsync(() => model.Editor.DeleteDtmfPreset((DtmfPresetViewModel)e.Preset)); };
        editor.UseTonePresetRequested += async (_, e) => { if (model is not null) await model.EditAsync(() => model.Editor.LoadTonePreset((TonePresetViewModel)e.Preset)); };
        editor.SendTonePresetRequested += async (_, e) => { if (model is not null) await model.SendPresetAsync((TonePresetViewModel)e.Preset); };
        editor.DeleteTonePresetRequested += async (_, e) => { if (model is not null) await model.EditAsync(() => model.Editor.DeleteTonePreset((TonePresetViewModel)e.Preset)); };
        editor.AddToneStepRequested += async (_, _) => { if (model is not null) await model.EditAsync(() => model.Editor.AddToneSequenceStep(false)); };
        editor.AddSilenceStepRequested += async (_, _) => { if (model is not null) await model.EditAsync(() => model.Editor.AddToneSequenceStep(true)); };
        editor.RemoveToneStepRequested += async (_, e) => { if (model is not null) await model.EditAsync(() => model.Editor.RemoveToneSequenceStep((ToneSequenceStepViewModel)e.Step)); };
        editor.MoveToneStepUpRequested += async (_, e) => { if (model is not null) await model.EditAsync(() => model.Editor.MoveToneSequenceStep((ToneSequenceStepViewModel)e.Step, -1)); };
        editor.MoveToneStepDownRequested += async (_, e) => { if (model is not null) await model.EditAsync(() => model.Editor.MoveToneSequenceStep((ToneSequenceStepViewModel)e.Step, 1)); };
        editor.SendQuickCallRequested += async (_, _) => { if (model is not null) await model.SendQuickCallAsync(); };
        editor.ImportAlertToneRequested += async (_, _) => await ImportAudioAsync();
        editor.SendAlertToneRequested += async (_, e) => { if (model is not null) await model.SendAlertAsync((AlertToneViewModel)e.Tone); };
        editor.DeleteAlertToneRequested += async (_, e) => { if (model is not null) await model.DeleteAlertAsync((AlertToneViewModel)e.Tone); };
        DetachedFromVisualTree += (_, _) =>
            DvmConsole.Threading.TaskObservation.Observe(DisposeAsync().AsTask());
    }

    public async ValueTask DisposeAsync()
    {
        if (model is not { } outgoing) return;
        model = null;
        editor.ImportPatternsAsync = null;
        editor.ExportPatterns = null;
        outgoing.PropertyChanged -= ModelChanged;
        await outgoing.DisposeAsync();
    }

    public async Task OpenAsync(MobileSession session, CancellationToken cancellationToken = default)
    {
        if (session.ToneSettings is not { } store) { status.Text = "Open a configuration to use tones."; editor.IsVisible = false; return; }
        try
        {
            var settings = await store.LoadToneSettingsAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            model = new(settings, store, session.Application, session.Assets);
            var opened = model;
            editor.ImportPatternsAsync = document => opened.EditAsync(() => opened.Editor.ImportPatterns(document));
            editor.ExportPatterns = opened.Editor.ExportPatterns;
            model.PropertyChanged += ModelChanged;
            monitor.IsChecked = model.LocalMonitorEnabled;
            editor.DataContext = model;
            DataContext = model;
            UpdateControls();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { status.Text = exception.Message; editor.IsVisible = false; }
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs args) => UpdateControls();

    private void UpdateControls()
    {
        if (model is null) return;
        updatingAssignment = true;
        try
        {
            AlertAssignment[] names = new[] { new AlertAssignment("") }.Concat(Enum.GetValues<LegacyAlertTone>().Select(tone => new AlertAssignment(tone.ToString(), (int)tone)))
                .Concat(model.AlertTones.Cast<AlertToneViewModel>().Select(tone => new AlertAssignment(tone.Name, AssetId: tone.AssetId)))
                .Concat(model.TonePresets.Cast<TonePresetViewModel>().Select(preset => new AlertAssignment(preset.Name))).ToArray();
            if (alertAssignment.ItemsSource is not AlertAssignment[] current || !current.SequenceEqual(names))
                alertAssignment.ItemsSource = names;
            alertAssignment.SelectedItem = names.FirstOrDefault(choice => choice.Name == model.MainAlertPresetName && choice.BuiltIn == model.MainAlertBuiltIn && choice.AssetId == model.MainAlertAssetId);
            alertAssignment.IsEnabled = !model.IsBusy;
        }
        finally { updatingAssignment = false; }
        status.Text = model.Status;
        editor.IsEnabled = !model.IsBusy;
        undo.IsEnabled = model.CanUndo && !model.IsBusy;
        save.IsVisible = model.HasUnsavedChanges || model.HasPendingAssetCleanup;
        save.Content = model.HasUnsavedChanges ? "Retry save" : "Retry cleanup";
        save.IsEnabled = !model.IsBusy;
        monitor.IsEnabled = !model.IsBusy;
        builtIns.IsEnabled = !model.IsBusy;
    }

    private async Task ImportAudioAsync()
    {
        var current = model;
        if (current is null || current.IsBusy) return;
        try
        {
            var picker = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("Document access is unavailable.");
            var files = await picker.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import alert audio",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Audio")
                    { Patterns = ["*.wav", "*.mp3", "*.ogg", "*.opus"], AppleUniformTypeIdentifiers = ["public.audio"] }]
            });
            try
            {
                if (files.Count == 0 || !ReferenceEquals(model, current)) return;
                var file = files[0];
                string mediaType = file.Name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ? "audio/mpeg"
                    : file.Name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? "audio/wav" : "audio/ogg";
                await using var source = await file.OpenReadAsync();
                await current.ImportAudioAsync(file.Name, mediaType, source);
            }
            finally { foreach (var file in files) file.Dispose(); }
        }
        catch (Exception exception) { status.Text = exception.Message; }
    }
}
