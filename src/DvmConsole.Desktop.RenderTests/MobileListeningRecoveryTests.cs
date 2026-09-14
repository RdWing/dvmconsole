// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DvmConsole.Application;
using DvmConsole.Mobile;
using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.RenderTests;

public sealed class MobileListeningRecoveryTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SpeakerResumesPausedAudioWithoutTogglingMuteAndAllowsRetry(bool fail)
    {
        await using var preview = new MobileConsoleView(ConsoleHostFormFactor.Phone);
        var execution = new ConsoleExecutionPolicy();
        execution.MediaServicesReset();
        var commands = new ListeningCommands();
        await using var session = new ConsoleApplicationSession(preview.ApplicationSession.Topology,
            ConsoleRuntimeSnapshot.Empty, commands);
        int attempts = 0;
        await using var view = new MobileConsoleView(ConsoleHostFormFactor.Phone,
            execution: execution, applicationSession: session, ownsSession: false,
            resumeListening: _ =>
            {
                attempts++;
                if (fail) throw new IOException("Output unavailable");
                execution.CompleteRecovery(execution.BeginRecovery(true)!.Value, true);
                return Task.CompletedTask;
            });
        var window = new Window { Width = 390, Height = 844, Content = view };
        try
        {
            window.Show(); window.UpdateLayout();
            var button = view.GetVisualDescendants().OfType<Button>()
                .Single(button => AutomationProperties.GetName(button) == "Resume listening");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, attempts);
            Assert.False(commands.OutputMuted);
            Assert.True(button.IsEnabled);
            if (fail)
            {
                Assert.False(execution.Snapshot.CanReceive);
                fail = false;
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(2, attempts);
            }
            Assert.True(execution.Snapshot.CanReceive);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(commands.OutputMuted);
        }
        finally { window.Close(); }
    }

    private sealed class ListeningCommands : IConsoleCommands, IConsoleListeningSettings
    {
        public bool OutputMuted { get; private set; }
        public bool CanSaveStartupPreference => false;
        public bool RestoreSelectedChannelsOnStartup => false;
        public ValueTask SetOutputMutedAsync(bool muted, CancellationToken token = default)
        { OutputMuted = muted; return ValueTask.CompletedTask; }
        public ValueTask SetRestoreSelectedChannelsAsync(bool restore, CancellationToken token = default)
            => ValueTask.CompletedTask;
        public ValueTask SetReceiveEnabledAsync(ChannelId id, bool enabled, CancellationToken token = default) => ValueTask.CompletedTask;
        public ValueTask<bool> BeginPttAsync(ChannelId id, CancellationToken token = default) => ValueTask.FromResult(false);
        public ValueTask EndPttAsync(ChannelId id, CancellationToken token = default) => ValueTask.CompletedTask;
        public ValueTask SetTransmitSelectedAsync(ChannelId id, bool selected, CancellationToken token = default) => ValueTask.CompletedTask;
        public ValueTask SetPageSelectedAsync(ChannelId id, bool selected, CancellationToken token = default) => ValueTask.CompletedTask;
        public ValueTask SetAlertSelectedAsync(ChannelId id, bool selected, CancellationToken token = default) => ValueTask.CompletedTask;
        public ValueTask SetTransmitEncryptedAsync(ChannelId id, bool encrypted, CancellationToken token = default) => ValueTask.CompletedTask;
        public ValueTask SetChannelGainAsync(ChannelId id, double gain, CancellationToken token = default) => ValueTask.CompletedTask;
        public ValueTask SetChannelBalanceAsync(ChannelId id, double balance, CancellationToken token = default) => ValueTask.CompletedTask;
    }
}
