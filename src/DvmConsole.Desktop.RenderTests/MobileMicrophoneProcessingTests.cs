// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DvmConsole.Application;
using DvmConsole.Audio;
using DvmConsole.Core.Settings;
using DvmConsole.Mobile;
using Xunit;

namespace DvmConsole.Desktop.RenderTests;

public sealed class MobileMicrophoneProcessingTests
{
    [AvaloniaTheory]
    [InlineData(320)]
    [InlineData(768)]
    public async Task ProcessingEditorAppliesValuesAndRejectsStaleSession(double width)
    {
        var commands = new Commands();
        var reference = new ConfigurationReference(ConfigurationId.New(), ConfigurationRevision.New());
        await using var application = new ConsoleApplicationSession(new(reference, [], [], []), ConsoleRuntimeSnapshot.Empty, commands);
        await using var replacement = new ConsoleApplicationSession(new(reference, [], [], []), ConsoleRuntimeSnapshot.Empty, commands);
        MobileSession current = new(application);
        var view = new MobileMicrophoneProcessingView(() => current);
        var window = new Window { Width = width, Height = 720, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            var numbers = view.GetVisualDescendants().OfType<Slider>().ToArray();
            Assert.Equal(5, numbers.Length);
            Assert.All(numbers, number => { Assert.True(number.Bounds.Width > 100); Assert.True(number.Bounds.Width <= width - 32); Assert.True(number.Bounds.Height >= 48); });
            var gain = numbers.Single(n => AutomationProperties.GetName(n) == "Mic gain");
            var target = numbers.Single(n => AutomationProperties.GetName(n) == "Microphone AGC target");
            Assert.Equal(1.5, MobileMicrophoneProcessingView.PositionToGain(gain.Value), 8);
            Assert.Equal(-12, gain.Minimum);
            Assert.Equal(12, gain.Maximum);
            Assert.All(numbers, number => Assert.Equal(0.25, number.TickFrequency));
            Assert.Equal(-40, target.Minimum);
            Assert.Equal(-12, target.Maximum);
            Assert.Equal(Math.Pow(10, 12.0 / 20), MobileMicrophoneProcessingView.PositionToGain(12), 8);
            Assert.False(target.IsEnabled);
            view.GetVisualDescendants().OfType<ToggleSwitch>().Single().IsChecked = true;
            Assert.True(target.IsEnabled);
            gain.Value = MobileMicrophoneProcessingView.GainToPosition(2);
            var apply = view.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Apply");
            async Task Apply()
            {
                apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            }
            await Apply();
            Assert.Equal(2, commands.MicrophoneProcessing.Gain, 8);
            Assert.True(commands.MicrophoneProcessing.AgcEnabled);
            commands.Fail = true;
            gain.Value = MobileMicrophoneProcessingView.GainToPosition(2.5);
            await Apply();
            Assert.Equal(2, commands.MicrophoneProcessing.Gain, 8);
            Assert.Equal(2.5, MobileMicrophoneProcessingView.PositionToGain(gain.Value), 8);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Storage unavailable");
            commands.Fail = false;
            async Task Click(string label)
            {
                view.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == label)
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            }
            var name = view.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Microphone preset name");
            name.Text = "Field";
            await Click("Save preset");
            Assert.Equal(2.5, Assert.Single(commands.MicrophonePresets.Presets).Gain, 8);
            gain.Value = MobileMicrophoneProcessingView.GainToPosition(1);
            await Click("Load preset");
            Assert.Equal(2.5, MobileMicrophoneProcessingView.PositionToGain(gain.Value), 8);
            Assert.True(view.GetVisualDescendants().OfType<ToggleSwitch>().Single().IsChecked);
            Assert.Equal(2, commands.MicrophoneProcessing.Gain, 8);
            await Click("Delete preset");
            Assert.Empty(commands.MicrophonePresets.Presets);
            await Click("Undo preset deletion");
            Assert.Equal("Field", Assert.Single(commands.MicrophonePresets.Presets).Name);
            current = new(replacement);
            commands.Fail = false;
            await Apply();
            Assert.Equal(2, commands.Saves);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.StartsWith("The active configuration changed", StringComparison.Ordinal) == true);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RoundedMicGainDisplayPreservesQuarterDecibelsAndAppliesUpperBound()
    {
        var commands = new Commands();
        await using var application = new ConsoleApplicationSession(new(null, [], [], []), ConsoleRuntimeSnapshot.Empty, commands);
        var current = new MobileSession(application);
        var view = new MobileMicrophoneProcessingView(() => current);
        var window = new Window { Width = 390, Height = 800, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            var slider = view.GetVisualDescendants().OfType<Slider>().Single(s => AutomationProperties.GetName(s) == "Mic gain");
            var value = view.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Mic gain (dB) exact value");
            slider.Value = 0.25;
            Assert.Equal(0.25.ToString("0.0"), value.Text);
            value.RaiseEvent(new RoutedEventArgs(Avalonia.Input.InputElement.LostFocusEvent));
            Assert.Equal(0.25, slider.Value);
            value.Text = "50";
            value.RaiseEvent(new RoutedEventArgs(Avalonia.Input.InputElement.LostFocusEvent));
            Assert.Equal(12, slider.Value);
            view.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Apply")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            Assert.Equal(Math.Pow(10, 12.0 / 20), commands.MicrophoneProcessing.Gain, 8);
            Assert.Equal(12, slider.Value);
        }
        finally { window.Close(); }
    }

    private sealed class Commands : IConsoleCommands, IConsoleMicrophoneProcessingSettings, IConsoleMicrophonePresets
    {
        public ConsoleMicrophonePresetCatalog MicrophonePresets { get; private set; } = ConsoleMicrophonePresetCatalog.Empty;
        public bool CanSaveMicrophonePresets => true;
        public ValueTask SaveMicrophonePresetAsync(string name, AudioInputProcessingOptions options, CancellationToken cancellationToken = default)
        {
            var preset = AudioInputPreset.Create(name, 0, options.Gain, options.LowGainDb, options.MidGainDb, options.HighGainDb);
            MicrophonePresets = new([preset], preset.Name);
            return ValueTask.CompletedTask;
        }
        public ValueTask DeleteMicrophonePresetAsync(string name, CancellationToken cancellationToken = default)
        { MicrophonePresets = ConsoleMicrophonePresetCatalog.Empty; return ValueTask.CompletedTask; }
        public AudioInputProcessingOptions MicrophoneProcessing { get; private set; } = new() { Gain = 1.5 };
        public bool CanSaveMicrophoneProcessing => true;
        public bool Fail { get; set; }
        public int Saves { get; private set; }
        public ValueTask SetMicrophoneProcessingAsync(AudioInputProcessingOptions options, CancellationToken cancellationToken = default)
        {
            Saves++;
            if (Fail) return ValueTask.FromException(new IOException("Storage unavailable"));
            MicrophoneProcessing = options;
            return ValueTask.CompletedTask;
        }
        public ValueTask SetReceiveEnabledAsync(ChannelId id, bool enabled, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<bool> BeginPttAsync(ChannelId id, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
        public ValueTask EndPttAsync(ChannelId id, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SetTransmitSelectedAsync(ChannelId id, bool selected, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SetPageSelectedAsync(ChannelId id, bool selected, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SetAlertSelectedAsync(ChannelId id, bool selected, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SetTransmitEncryptedAsync(ChannelId id, bool encrypted, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SetChannelGainAsync(ChannelId id, double gain, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SetChannelBalanceAsync(ChannelId id, double balance, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
