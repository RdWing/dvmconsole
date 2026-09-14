// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using DvmConsole.Application;
using DvmConsole.Vocoder;

namespace DvmConsole.Mobile;

internal sealed class MobileReceiveProcessingView : UserControl
{
    public event EventHandler? SettingsRequested;
    private readonly Func<MobileSession> session;
    private readonly IConsoleApplicationSession owner;
    private readonly IConsoleReceiveProcessingSettings? settings;
    private readonly ComboBox modes = new() { MinHeight = 48, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Dictionary<VocoderMode, ModeEditor> editors = [];
    private readonly ContentControl editor = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel fields = new() { Spacing = 12 };
    private bool saving;
    private readonly IConsoleDmrReceiveKeySettings? keySettings;
    private readonly StackPanel keyFields = new() { Spacing = 8 };
    private readonly ToggleSwitch requireKey = new()
    {
        Content = new TextBlock { Text = "Require configured DMR receive key", TextWrapping = TextWrapping.Wrap },
        MinHeight = 48
    };

    public MobileReceiveProcessingView(Func<MobileSession> session)
    {
        this.session = session;
        owner = session().Application;
        settings = owner.Commands as IConsoleReceiveProcessingSettings;
        keySettings = owner.Commands as IConsoleDmrReceiveKeySettings;
        var back = new Button { Content = "‹ Audio", MinHeight = 48 };
        back.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        fields.Children.Add(new TextBlock { Text = "Adjust receive enhancement for each digital protocol. Apply restarts listening decoders and may briefly interrupt local audio; patch forwarding is unchanged.", TextWrapping = TextWrapping.Wrap });
        AutomationProperties.SetName(modes, "Receive Processing protocol");
        modes.ItemsSource = ConsoleReceiveProcessingProfile.Modes;
        modes.ItemTemplate = new FuncDataTemplate<ConsoleReceiveProcessingMode>((mode, _) => new TextBlock { Text = mode?.Name });
        foreach (var mode in ConsoleReceiveProcessingProfile.Modes)
            editors[mode.Mode] = new(settings?.ReceiveProcessing.GetValueOrDefault(mode.Mode) ?? new());
        modes.SelectionChanged += (_, _) =>
        {
            if (modes.SelectedItem is ConsoleReceiveProcessingMode mode) editor.Content = editors[mode.Mode];
        };
        fields.Children.Add(modes);
        fields.Children.Add(editor);
        var apply = new Button { Content = "Apply receive processing", MinHeight = 48 };
        apply.Click += async (_, _) => await SaveAsync();
        fields.Children.Add(apply);
        var body = new StackPanel { Spacing = 12 };
        requireKey.IsChecked = keySettings?.RequireConfiguredDmrReceiveKey == true;
        AutomationProperties.SetName(requireKey, "Require configured DMR receive key");
        keyFields.Children.Add(requireKey);
        keyFields.Children.Add(new TextBlock
        {
            Text = "Require the channel's configured algorithm and key ID, or use on-air key identifiers within each FNE system. Applying restarts receive and patch decoders; interrupted patch calls are not replayed.",
            TextWrapping = TextWrapping.Wrap
        });
        var applyKeys = new Button { Content = "Apply DMR key policy", MinHeight = 48 };
        applyKeys.Click += async (_, _) => await SaveKeyPolicyAsync();
        keyFields.Children.Add(applyKeys);
        keyFields.IsEnabled = keySettings?.CanSaveDmrReceiveKeyPolicy == true;
        keyFields.IsVisible = keySettings is not null;
        body.Children.Add(keyFields);
        body.Children.Add(fields);
        body.Children.Add(status);
        Content = MobileSettingsPageLayout.Create(MobileSettingsPageLayout.Heading("Receive Processing", back), body);
        fields.IsEnabled = settings?.CanSaveReceiveProcessing == true;
        if (!fields.IsEnabled) status.Text = "Open a configuration first.";
        modes.SelectedIndex = 0;
    }

    private async Task SaveKeyPolicyAsync()
    {
        if (saving || keySettings?.CanSaveDmrReceiveKeyPolicy != true) return;
        if (!ReferenceEquals(owner, session().Application))
        { status.Text = "The active configuration changed. Reopen receive processing from Settings."; return; }
        saving = true;
        keyFields.IsEnabled = fields.IsEnabled = false;
        try
        {
            await keySettings.SetRequireConfiguredDmrReceiveKeyAsync(requireKey.IsChecked == true);
            status.Text = ReferenceEquals(owner, session().Application)
                ? owner.Snapshot.StatusText : "Saved for the previous session. Reopen this page for the active configuration.";
        }
        catch (Exception exception) { status.Text = exception.Message; }
        finally
        {
            requireKey.IsChecked = keySettings.RequireConfiguredDmrReceiveKey;
            saving = false;
            RestoreAvailability();
        }
    }

    private void RestoreAvailability()
    {
        bool current = ReferenceEquals(owner, session().Application);
        fields.IsEnabled = current && settings?.CanSaveReceiveProcessing == true;
        keyFields.IsEnabled = current && keySettings?.CanSaveDmrReceiveKeyPolicy == true;
    }

    private async Task SaveAsync()
    {
        if (saving || settings?.CanSaveReceiveProcessing != true || modes.SelectedItem is not ConsoleReceiveProcessingMode mode) return;
        if (!ReferenceEquals(owner, session().Application))
        { status.Text = "The active configuration changed. Reopen receive processing from Settings."; return; }
        saving = true;
        keyFields.IsEnabled = fields.IsEnabled = false;
        try
        {
            await settings.SetReceiveProcessingAsync(mode.Mode, editors[mode.Mode].Capture());
            editors[mode.Mode].Restore(settings.ReceiveProcessing[mode.Mode]);
            status.Text = ReferenceEquals(owner, session().Application) ? "Receive Processing saved." : "Saved for the previous session. Reopen this page for the active configuration.";
        }
        catch (Exception exception) { status.Text = exception.Message; }
        finally { saving = false; RestoreAvailability(); }
    }

    private sealed class ModeEditor : StackPanel
    {
        private readonly ToggleSwitch highPass = new() { Content = "High-pass filter", MinHeight = 48 };
        private readonly ToggleSwitch peaking = new() { Content = "Peaking filter", MinHeight = 48 };
        private readonly ToggleSwitch compressor = new() { Content = "Compressor", MinHeight = 48 };
        private readonly Slider highPassHz, peakingHz, peakingGain, ratio, threshold, makeup;

        public ModeEditor(ReceiveAudioProcessingOptions options)
        {
            Spacing = 8;
            highPass.IsChecked = options.HighPassFilterEnabled;
            peaking.IsChecked = options.PeakingFilterEnabled;
            compressor.IsChecked = options.CompressorEnabled;
            Children.Add(highPass);
            highPassHz = Number("High-pass frequency (Hz)", 0, 500, options.HighPassFrequencyHz, 25);
            Children.Add(peaking);
            peakingHz = Number("Peaking frequency (Hz)", 250, 3000, options.PeakingFrequencyHz, 25);
            peakingGain = Number("Peaking gain (dB)", -10, 10, options.PeakingGainDb);
            Children.Add(compressor);
            ratio = Number("Compressor ratio", 1, 10, options.CompressorRatio);
            threshold = Number("Compressor threshold (dBFS)", -40, 0, options.CompressorThresholdDbfs);
            makeup = Number("Compressor makeup (dB)", 0, 10, options.CompressorMakeupGainDb);
        }

        public void Restore(ReceiveAudioProcessingOptions options)
        {
            highPass.IsChecked = options.HighPassFilterEnabled;
            highPassHz.Value = (double)options.HighPassFrequencyHz;
            peaking.IsChecked = options.PeakingFilterEnabled;
            peakingHz.Value = (double)options.PeakingFrequencyHz;
            peakingGain.Value = (double)options.PeakingGainDb;
            compressor.IsChecked = options.CompressorEnabled;
            ratio.Value = (double)options.CompressorRatio;
            threshold.Value = (double)options.CompressorThresholdDbfs;
            makeup.Value = (double)options.CompressorMakeupGainDb;
        }

        private Slider Number(string label, double min, double max, float value, double increment = 1)
        {
            var number = new Slider
            {
                Minimum = min,
                Maximum = max,
                TickFrequency = increment,
                SmallChange = increment,
                IsSnapToTickEnabled = true,
                Value = (double)value,
                MinHeight = 48,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetName(number, label);
            Children.Add(MobileAudioSlider.Field(label, number));
            return number;
        }

        private static float Value(Slider number) => (float)number.Value;

        public ReceiveAudioProcessingOptions Capture() => new()
        {
            HighPassFilterEnabled = highPass.IsChecked == true,
            HighPassFrequencyHz = Value(highPassHz),
            PeakingFilterEnabled = peaking.IsChecked == true,
            PeakingFrequencyHz = Value(peakingHz),
            PeakingGainDb = Value(peakingGain),
            CompressorEnabled = compressor.IsChecked == true,
            CompressorRatio = Value(ratio),
            CompressorThresholdDbfs = Value(threshold),
            CompressorMakeupGainDb = Value(makeup)
        };
    }
}
