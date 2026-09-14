// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using DvmConsole.Application;
using DvmConsole.Audio;
using DvmConsole.Core.Settings;

namespace DvmConsole.Mobile;

internal sealed class MobileMicrophoneProcessingView : UserControl
{
    private readonly Func<MobileSession> session;
    private readonly IConsoleApplicationSession owner;
    private readonly IConsoleMicrophoneProcessingSettings? settings;
    private readonly Slider gain = Number("Mic gain", -12, 12, 0.25);
    private readonly Slider low = Number("Microphone low EQ gain", -12, 12, 0.25);
    private readonly Slider mid = Number("Microphone mid EQ gain", -12, 12, 0.25);
    private readonly Slider high = Number("Microphone high EQ gain", -12, 12, 0.25);
    private readonly Slider target = Number("Microphone AGC target", -40, -12, 0.25);
    private readonly ToggleSwitch agc = new() { Content = "Automatic gain control", MinHeight = 48 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel fields = new() { Spacing = 10 };
    private readonly ComboBox presets = new() { MinHeight = 48, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox presetName = new() { Watermark = "Preset name", MaxLength = 80, MinHeight = 48 };
    private readonly Button undoPreset = new() { Content = "Undo preset deletion", MinHeight = 48, IsVisible = false };
    private AudioInputPreset? deletedPreset;
    private bool saving;
    public event EventHandler? SettingsRequested;

    public MobileMicrophoneProcessingView(Func<MobileSession> session)
    {
        this.session = session;
        owner = session().Application;
        settings = owner.Commands as IConsoleMicrophoneProcessingSettings;
        var back = new Button { Content = "‹ Audio", MinHeight = 48 };
        back.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        fields.Children.Add(new TextBlock { Text = "DVM Console processing adjusts microphone audio before transmission. Changes apply to the next manual PTT call.", TextWrapping = TextWrapping.Wrap });
        AddField("Mic gain (dB)", gain);
        AddField("Low EQ (dB)", low);
        AddField("Mid EQ (dB)", mid);
        AddField("High EQ (dB)", high);
        fields.Children.Add(agc);
        AddField("AGC target (dBFS)", target);
        agc.IsCheckedChanged += (_, _) => target.IsEnabled = agc.IsChecked == true;
        var processingRows = fields.Children.Skip(1).ToArray();
        foreach (var row in processingRows) fields.Children.Remove(row);
        fields.Children.Add(MobileSettingsSurface.Group(processingRows, paddedRows: true));
        var apply = new Button { Content = "Apply", MinHeight = 48 };
        var reset = new Button { Content = "Reset fields to defaults", MinHeight = 48 };
        apply.Click += async (_, _) => await SaveAsync();
        reset.Click += (_, _) => Populate(new AudioInputProcessingOptions());
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
        actions.Children.Add(apply);
        actions.Children.Add(reset);
        fields.Children.Add(actions);
        int presetStart = fields.Children.Count;
        AddPresets();
        var presetControls = fields.Children.Skip(presetStart).ToArray();
        foreach (var control in presetControls) fields.Children.Remove(control);
        if (presetControls.Length > 0)
            fields.Children.Add(MobileSettingsSurface.Group(presetControls, paddedRows: true));
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(fields);
        body.Children.Add(status);
        Content = MobileSettingsPageLayout.Create(MobileSettingsPageLayout.Heading("Microphone Processing", back), body);
        fields.IsEnabled = settings?.CanSaveMicrophoneProcessing == true;
        if (!fields.IsEnabled) status.Text = "Open a configuration with microphone transmission available first.";
        Populate(settings?.MicrophoneProcessing ?? new());
    }

    private void AddPresets()
    {
        if (owner.Commands is not IConsoleMicrophonePresets { CanSaveMicrophonePresets: true }) return;
        fields.Children.Add(new TextBlock { Text = "Microphone presets", FontWeight = FontWeight.SemiBold });
        fields.Children.Add(new TextBlock { Text = "Presets store gain and EQ. Loading fills the editor; choose Apply to use it. AGC stays unchanged.", TextWrapping = TextWrapping.Wrap });
        AutomationProperties.SetName(presets, "Microphone preset");
        AutomationProperties.SetName(presetName, "Microphone preset name");
        presets.ItemTemplate = new FuncDataTemplate<AudioInputPreset>((preset, _) =>
            new TextBlock { Text = preset?.Name, TextWrapping = TextWrapping.Wrap });
        fields.Children.Add(presets);
        fields.Children.Add(presetName);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
        var load = new Button { Content = "Load preset", MinHeight = 48 };
        var save = new Button { Content = "Save preset", MinHeight = 48 };
        var delete = new Button { Content = "Delete preset", MinHeight = 48 };
        actions.Children.Add(load);
        actions.Children.Add(save);
        actions.Children.Add(delete);
        fields.Children.Add(actions);
        fields.Children.Add(undoPreset);
        load.Click += (_, _) =>
        {
            if (!IsCurrent()) return;
            if (presets.SelectedItem is not AudioInputPreset preset) { status.Text = "Choose a preset first."; return; }
            gain.Value = GainToPosition(preset.Gain);
            low.Value = (double)preset.LowGainDb;
            mid.Value = (double)preset.MidGainDb;
            high.Value = (double)preset.HighGainDb;
            presetName.Text = preset.Name;
            status.Text = "Preset loaded into the editor. Choose Apply to use it.";
        };
        save.Click += async (_, _) => await RunPresetAsync(async commands =>
        {
            double g = PositionToGain(gain.Value), l = low.Value, m = mid.Value, h = high.Value;
            await commands.SaveMicrophonePresetAsync(presetName.Text ?? string.Empty, new()
            { Gain = (double)g, LowGainDb = (double)l, MidGainDb = (double)m, HighGainDb = (double)h });
        }, "Preset saved. Choose Apply to use the current processing values.");
        delete.Click += async (_, _) => await RunPresetAsync(async commands =>
        {
            if (presets.SelectedItem is not AudioInputPreset preset) throw new InvalidOperationException("Choose a preset first.");
            await commands.DeleteMicrophonePresetAsync(preset.Name);
            deletedPreset = preset;
            undoPreset.IsVisible = true;
        }, "Preset deleted. Current processing is unchanged.");
        undoPreset.Click += async (_, _) => await RunPresetAsync(async commands =>
        {
            if (deletedPreset is not { } preset) return;
            if (commands.MicrophonePresets.Presets.Any(existing => AudioInputPreset.NamesEqual(existing.Name, preset.Name)))
                throw new InvalidOperationException("A preset with that name now exists. Undo would replace it.");
            await commands.SaveMicrophonePresetAsync(preset.Name, new()
            { Gain = preset.Gain, LowGainDb = preset.LowGainDb, MidGainDb = preset.MidGainDb, HighGainDb = preset.HighGainDb });
            deletedPreset = null;
            undoPreset.IsVisible = false;
        }, "Preset restored.");
        RefreshPresets();
    }

    private bool IsCurrent()
    {
        if (ReferenceEquals(owner, session().Application)) return true;
        status.Text = "The active configuration changed. Reopen microphone processing from Settings.";
        return false;
    }

    private void RefreshPresets()
    {
        if (owner.Commands is not IConsoleMicrophonePresets commands) return;
        var catalog = commands.MicrophonePresets;
        presets.ItemsSource = catalog.Presets;
        presets.SelectedItem = catalog.Presets.FirstOrDefault(preset => AudioInputPreset.NamesEqual(preset.Name, catalog.SelectedName));
        presetName.Text = catalog.SelectedName;
    }

    private async Task RunPresetAsync(Func<IConsoleMicrophonePresets, Task> action, string success)
    {
        if (saving || !IsCurrent() || owner.Commands is not IConsoleMicrophonePresets commands) return;
        saving = true;
        fields.IsEnabled = false;
        try
        {
            await action(commands);
            if (IsCurrent()) { RefreshPresets(); status.Text = success; }
        }
        catch (Exception exception) { status.Text = exception.Message; }
        finally { saving = false; fields.IsEnabled = ReferenceEquals(owner, session().Application); }
    }

    internal static double GainToPosition(double value) => Math.Clamp(20 * Math.Log10(Math.Clamp(value, 0.25, 4)), -12, 12);
    internal static double PositionToGain(double position) => Math.Pow(10, Math.Clamp(position, -12, 12) / 20);

    private static Slider Number(string name, double minimum, double maximum, double increment)
    {
        var control = new Slider
        {
            Minimum = minimum,
            Maximum = maximum,
            TickFrequency = increment,
            SmallChange = increment,
            IsSnapToTickEnabled = true,
            MinHeight = 48,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(control, name);
        return control;
    }

    private void AddField(string label, Control control)
    {
        fields.Children.Add(MobileAudioSlider.Field(label, (Slider)control, format: "0.0", snapEnteredValue: true));
    }

    private void Populate(AudioInputProcessingOptions options)
    {
        gain.Value = GainToPosition(options.Gain);
        low.Value = (double)options.LowGainDb;
        mid.Value = (double)options.MidGainDb;
        high.Value = (double)options.HighGainDb;
        agc.IsChecked = options.AgcEnabled;
        target.Value = (double)options.AgcTargetDbfs;
        target.IsEnabled = options.AgcEnabled;
    }

    private async Task SaveAsync()
    {
        if (saving || settings?.CanSaveMicrophoneProcessing != true) return;
        if (!ReferenceEquals(owner, session().Application))
        { status.Text = "The active configuration changed. Reopen microphone processing from Settings."; return; }
        double gainValue = PositionToGain(gain.Value), lowValue = low.Value, midValue = mid.Value,
            highValue = high.Value, targetValue = target.Value;
        saving = true;
        fields.IsEnabled = false;
        try
        {
            await settings.SetMicrophoneProcessingAsync(new()
            {
                Gain = (double)gainValue,
                LowGainDb = (double)lowValue,
                MidGainDb = (double)midValue,
                HighGainDb = (double)highValue,
                AgcEnabled = agc.IsChecked == true,
                AgcTargetDbfs = (double)targetValue
            });
            if (ReferenceEquals(owner, session().Application))
            { Populate(settings.MicrophoneProcessing); status.Text = "Saved for the next manual transmission."; }
            else status.Text = "The previous session's settings were saved. Reopen this page for the active configuration.";
        }
        catch (Exception exception) { status.Text = exception.Message; }
        finally { saving = false; fields.IsEnabled = ReferenceEquals(owner, session().Application); }
    }
}
