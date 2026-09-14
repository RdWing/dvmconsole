// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DvmConsole.Application;
using DvmConsole.Mobile;
using Xunit;

namespace DvmConsole.Desktop.RenderTests;

public sealed class MobileRecordingSettingsTests
{
    [AvaloniaTheory]
    [InlineData(320)]
    [InlineData(768)]
    public async Task RetentionRequiresReviewAndPreservesFailedOrStaleEdits(double width)
    {
        var commands = new Commands();
        var reference = new ConfigurationReference(ConfigurationId.New(), ConfigurationRevision.New());
        await using var application = new ConsoleApplicationSession(new(reference, [], [], []), ConsoleRuntimeSnapshot.Empty, commands);
        await using var replacement = new ConsoleApplicationSession(new(reference, [], [], []), ConsoleRuntimeSnapshot.Empty, commands);
        MobileSession current = new(application);
        var view = new MobileRecordingSettingsView(() => current);
        var window = new Window { Width = width, Height = 720, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            var days = view.GetVisualDescendants().OfType<NumericUpDown>().Single();
            var review = view.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Review retention");
            var apply = view.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Apply reviewed retention");
            async Task Click(Button button)
            {
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            }
            Assert.False(apply.IsEnabled);
            await Click(apply);
            Assert.Equal(0, commands.Saves);
            Assert.InRange(days.Bounds.Width, 100, width - 32);
            Assert.True(days.Bounds.Height >= 48);
            await Click(review);
            Assert.True(apply.IsEnabled);
            days.Value = 0;
            Assert.False(apply.IsEnabled);
            await Click(review);
            await Click(apply);
            Assert.Equal(new RecordingRetentionPolicy(0, true), commands.RecordingRetention);
            days.Value = 30;
            await Click(review);
            commands.Fail = true;
            await Click(apply);
            Assert.Equal(30m, days.Value);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Storage unavailable");
            current = new(replacement);
            await Click(apply);
            Assert.Equal(2, commands.Saves);
        }
        finally { window.Close(); }
    }

    private sealed class Commands : IConsoleCommands, IConsoleRecordingSettings
    {
        public RecordingRetentionPolicy RecordingRetention { get; private set; } = new();
        public bool CanSaveRecordingRetention => true;
        public bool Fail { get; set; }
        public int Saves { get; private set; }
        public Task<RecordingRetentionPreview> PreviewRecordingRetentionAsync(int days, CancellationToken cancellationToken = default)
            => Task.FromResult(new RecordingRetentionPreview(days == 0 ? null : DateTimeOffset.UtcNow.AddDays(-days), days == 0 ? 0 : 2));
        public ValueTask SetRecordingRetentionAsync(RecordingRetentionPolicy policy, CancellationToken cancellationToken = default)
        {
            Saves++;
            if (Fail) return ValueTask.FromException(new IOException("Storage unavailable"));
            RecordingRetention = policy;
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
