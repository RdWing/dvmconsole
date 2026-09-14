// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DvmConsole.Application;
using DvmConsole.Mobile;
using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Desktop.RenderTests;

public sealed class MobileReceiveProcessingTests
{
    [AvaloniaTheory]
    [InlineData(320)]
    [InlineData(768)]
    public async Task ReceiveEditorKeepsProtocolDraftsAndRejectsStaleApply(double width)
    {
        var commands = new Commands();
        var reference = new ConfigurationReference(ConfigurationId.New(), ConfigurationRevision.New());
        await using var application = new ConsoleApplicationSession(new(reference, [], [], []), ConsoleRuntimeSnapshot.Empty, commands);
        await using var replacement = new ConsoleApplicationSession(new(reference, [], [], []), ConsoleRuntimeSnapshot.Empty, commands);
        MobileSession current = new(application);
        var view = new MobileReceiveProcessingView(() => current);
        var window = new Window { Width = width, Height = 720, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            var modes = view.GetVisualDescendants().OfType<ComboBox>().Single();
            Slider Frequency() => view.GetVisualDescendants().OfType<Slider>()
                .Single(n => AutomationProperties.GetName(n) == "High-pass frequency (Hz)");
            Frequency().Value = 170;
            modes.SelectedIndex = 2;
            window.UpdateLayout();
            Assert.Equal(250d, Frequency().Value);
            Frequency().Value = 200;
            modes.SelectedIndex = 0;
            window.UpdateLayout();
            Assert.Equal(170d, Frequency().Value);
            Assert.All(view.GetVisualDescendants().OfType<Slider>(), n =>
            { Assert.True(n.Bounds.Width > 100); Assert.True(n.Bounds.Width <= width - 32); Assert.True(n.Bounds.Height >= 48); });
            var apply = view.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Apply receive processing");
            async Task Apply()
            {
                apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            }
            await Apply();
            Assert.Equal(170, commands.ReceiveProcessing[VocoderMode.P25Imbe].HighPassFrequencyHz);
            Assert.Equal(250, commands.ReceiveProcessing[VocoderMode.DmrAmbe].HighPassFrequencyHz);
            var keyToggle = view.GetVisualDescendants().OfType<ToggleSwitch>().Single(c =>
                AutomationProperties.GetName(c) == "Require configured DMR receive key");
            var applyKeys = view.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Apply DMR key policy");
            keyToggle.IsChecked = true;
            applyKeys.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(commands.RequireConfiguredDmrReceiveKey);
            commands.Fail = true;
            keyToggle.IsChecked = false;
            applyKeys.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(keyToggle.IsChecked);
            Assert.True(commands.RequireConfiguredDmrReceiveKey);
            Frequency().Value = 190;
            await Apply();
            Assert.Equal(170, commands.ReceiveProcessing[VocoderMode.P25Imbe].HighPassFrequencyHz);
            Assert.Equal(190d, Frequency().Value);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Storage unavailable");
            current = new(replacement);
            applyKeys.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(2, commands.KeySaves);
            await Apply();
            Assert.Equal(2, commands.Saves);
        }
        finally { window.Close(); }
    }

    private sealed class Commands : IConsoleCommands, IConsoleReceiveProcessingSettings, IConsoleDmrReceiveKeySettings
    {
        public ImmutableDictionary<VocoderMode, ReceiveAudioProcessingOptions> ReceiveProcessing { get; private set; } = ConsoleReceiveProcessingProfile.Defaults;
        public bool CanSaveReceiveProcessing => true;
        public bool RequireConfiguredDmrReceiveKey { get; private set; }
        public bool CanSaveDmrReceiveKeyPolicy => true;
        public int KeySaves { get; private set; }
        public ValueTask SetRequireConfiguredDmrReceiveKeyAsync(bool required, CancellationToken cancellationToken = default)
        {
            KeySaves++;
            if (Fail) return ValueTask.FromException(new IOException("Storage unavailable"));
            RequireConfiguredDmrReceiveKey = required;
            return ValueTask.CompletedTask;
        }
        public bool Fail { get; set; }
        public int Saves { get; private set; }
        public ValueTask SetReceiveProcessingAsync(VocoderMode mode, ReceiveAudioProcessingOptions options, CancellationToken cancellationToken = default)
        {
            Saves++;
            if (Fail) return ValueTask.FromException(new IOException("Storage unavailable"));
            ReceiveProcessing = ReceiveProcessing.SetItem(mode, options);
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
