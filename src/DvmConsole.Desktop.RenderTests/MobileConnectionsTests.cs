// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
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

public sealed class MobileConnectionsTests
{
    [AvaloniaFact]
    public async Task IndividualConnectionsSerializeActionsReuseRowsAndRejectRetiredButtons()
    {
        await using var console = new MobileConsoleView(ConsoleHostFormFactor.Phone);
        await using var replacement = new MobileConsoleView(ConsoleHostFormFactor.Phone);
        var commands = new Connections();
        var current = new MobileSession(console.ApplicationSession, commands);
        var view = new MobileConnectionsView(() => current);
        var window = new Window { Width = 390, Height = 700, Content = view };
        Task? operation = null;
        try
        {
            window.Show();
            window.UpdateLayout();
            Button alpha = view.GetVisualDescendants().OfType<Button>()
                .Single(button => AutomationProperties.GetName(button) == "Connect Alpha");
            view.Refresh();
            Assert.Contains(alpha, view.GetVisualDescendants());
            operation = view.ToggleAsync(SystemId.FromName("Alpha"));
            Assert.Equal(SystemId.FromName("Alpha"), Assert.Single(commands.Toggled));
            Assert.All(view.GetVisualDescendants().OfType<Button>(), button => Assert.False(button.IsEnabled));
            await view.ToggleAsync(SystemId.FromName("Beta"));
            Assert.Single(commands.Toggled);
            commands.Release.TrySetResult();
            await operation;
            Assert.True(alpha.IsEnabled);

            current = new MobileSession(replacement.ApplicationSession, commands);
            alpha.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Assert.Single(commands.Toggled);
            Assert.DoesNotContain(alpha, view.GetVisualDescendants());
        }
        finally
        {
            commands.Release.TrySetResult();
            if (operation is not null) await operation;
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(320)]
    [InlineData(768)]
    public async Task ChimePreferenceRestoresFailedSavesAndRejectsStaleControls(double width)
    {
        var settings = new CueCommands();
        var nextSettings = new CueCommands();
        var reference = new ConfigurationReference(ConfigurationId.New(), ConfigurationRevision.New());
        await using var application = new ConsoleApplicationSession(new(reference, [], [], []), ConsoleRuntimeSnapshot.Empty, settings);
        await using var replacement = new ConsoleApplicationSession(new(reference, [], [], []), ConsoleRuntimeSnapshot.Empty, nextSettings);
        MobileSession current = new(application);
        var view = new MobileConnectionsView(() => current);
        var window = new Window { Width = width, Height = 700, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            var toggle = view.GetVisualDescendants().OfType<ToggleSwitch>().Single();
            Assert.True(toggle.IsVisible);
            Assert.True(toggle.Bounds.Height >= 48);
            Assert.Equal(0, settings.Saves);
            toggle.IsChecked = true;
            Assert.True(settings.ConnectionChimes);
            settings.Fail = true;
            toggle.IsChecked = false;
            Assert.True(toggle.IsChecked);
            Assert.True(settings.ConnectionChimes);
            current = new(replacement);
            toggle.IsChecked = false;
            Assert.Equal(2, settings.Saves);
            Assert.Equal(0, nextSettings.Saves);
            Assert.False(toggle.IsChecked);
        }
        finally { window.Close(); }
    }

    private sealed class CueCommands : IConsoleCommands, IConsoleConnectionCueSettings
    {
        public bool ConnectionChimes { get; private set; }
        public bool CanSaveConnectionChimes => true;
        public bool Fail;
        public int Saves;
        public ValueTask SetConnectionChimesAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            Saves++;
            if (Fail) return ValueTask.FromException(new IOException("Storage unavailable"));
            ConnectionChimes = enabled;
            return ValueTask.CompletedTask;
        }
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

    private sealed class Connections : IConsoleConnectionCommands, IConsoleConnectionStateSource
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<SystemId> Toggled { get; } = [];
        public ImmutableArray<RadioConnectionSnapshot> ConnectionStates { get; } =
        [
            new(SystemId.FromName("Alpha"), "Alpha", RadioConnectionState.Disconnected, "Idle", DateTimeOffset.UnixEpoch),
            new(SystemId.FromName("Beta"), "Beta", RadioConnectionState.Disconnected, "Idle", DateTimeOffset.UnixEpoch)
        ];
        public Task ToggleAsync(SystemId id, CancellationToken cancellationToken = default)
        {
            Toggled.Add(id);
            return Release.Task;
        }
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RestoreAsync(IEnumerable<SystemId> ids, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
