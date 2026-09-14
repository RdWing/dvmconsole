// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DvmConsole.Application;

namespace DvmConsole.Mobile;

internal sealed class MobileSubscriberCommandsView : UserControl
{
    public event EventHandler? SettingsRequested;
    private static readonly string[] CommandNames = ["Page", "Radio check", "Inhibit", "Uninhibit"];

    public MobileSubscriberCommandsView(MobileSession session)
    {
        var commands = session.Application.Commands as IConsoleSubscriberCommands;
        var connections = session.Application.Commands as IConsoleConnectionStateNotifications;
        var historyNotifications = commands as IConsoleSubscriberHistoryNotifications;
        var targets = commands?.SubscriberTargets.ToArray() ?? [];
        var back = new Button { Content = "‹ Settings", MinHeight = 44 };
        var body = new StackPanel { Spacing = 12 };
        var status = new TextBlock { Name = "SubscriberCommandStatus", TextWrapping = TextWrapping.Wrap };
        body.Children.Add(status);
        var readiness = new TextBlock { TextWrapping = TextWrapping.Wrap };
        body.Children.Add(readiness);
        var system = new ComboBox
        {
            ItemsSource = targets.Select(target => target.Name).ToArray(),
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = targets.Length == 0 ? -1 : 0
        };
        var command = new ComboBox
        {
            ItemsSource = CommandNames,
            SelectedIndex = 0,
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var destination = new TextBox { Watermark = "1–16777215", MinHeight = 44 };
        AutomationProperties.SetName(system, "FNE system");
        AutomationProperties.SetName(command, "Subscriber command");
        AutomationProperties.SetName(destination, "Destination subscriber RID");
        body.Children.Add(new TextBlock { Text = "FNE system" }); body.Children.Add(system);
        body.Children.Add(new TextBlock { Text = "Command" }); body.Children.Add(command);
        body.Children.Add(new TextBlock { Text = "Destination subscriber RID" }); body.Children.Add(destination);
        var acknowledgement = new CheckBox { MinHeight = 44, IsVisible = false };
        var acknowledgementText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        acknowledgement.Content = acknowledgementText;
        body.Children.Add(acknowledgement);
        var send = new Button { Content = "Send", MinHeight = 44, MinWidth = 100 };
        body.Children.Add(send);
        body.Children.Add(new TextBlock { Text = "Recent commands", FontWeight = FontWeight.SemiBold });
        var history = new StackPanel { Spacing = 8 };
        body.Children.Add(history);
        bool sending = false;
        bool attached = false;
        Guid? displayedAttempt = null;
        System.Collections.Immutable.ImmutableList<ConsoleSubscriberCommandResult>? renderedHistory = null;
        bool HasValidDestination() => uint.TryParse(destination.Text?.Trim(), NumberStyles.None,
            CultureInfo.InvariantCulture, out uint rid) && rid is > 0 and <= 0xFFFFFF;
        void UpdateAvailability()
        {
            var selected = system.SelectedIndex >= 0 ? targets[system.SelectedIndex] : null;
            bool connected = selected is not null && commands?.SubscriberTargets.Any(target =>
                target.Id == selected.Id && target.IsConnected) == true;
            readiness.Text = selected is null ? "Open a configuration with an FNE system to send subscriber commands."
                : !connected ? "Connect this system in Settings before sending subscriber commands." : string.Empty;
            readiness.IsVisible = readiness.Text.Length != 0;
            send.IsEnabled = !sending && connected && !session.Application.Snapshot.IsQuiescing && HasValidDestination() &&
                (!acknowledgement.IsVisible || acknowledgement.IsChecked == true);
        }
        void OnSnapshotChanged(object? sender, ConsoleSnapshotChangedEventArgs args)
            => Dispatcher.UIThread.Post(() => { if (attached) UpdateAvailability(); });
        void OnConnectionChanged(object? sender, EventArgs args)
            => Dispatcher.UIThread.Post(() => { if (attached) UpdateAvailability(); });
        void OnHistoryChanged(object? sender, EventArgs args)
            => Dispatcher.UIThread.Post(() => { if (attached) RefreshHistory(); });
        AttachedToVisualTree += (_, _) =>
        {
            attached = true;
            session.Application.SnapshotChanged += OnSnapshotChanged;
            if (historyNotifications is not null) historyNotifications.SubscriberHistoryChanged += OnHistoryChanged;
            RefreshHistory();
            if (connections is not null) connections.ConnectionStatesChanged += OnConnectionChanged;
            UpdateAvailability();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            attached = false;
            session.Application.SnapshotChanged -= OnSnapshotChanged;
            if (historyNotifications is not null) historyNotifications.SubscriberHistoryChanged -= OnHistoryChanged;
            if (connections is not null) connections.ConnectionStatesChanged -= OnConnectionChanged;
        };
        void ResetAcknowledgement()
        {
            acknowledgement.IsChecked = false;
            acknowledgement.IsVisible = command.SelectedIndex >= 2;
            acknowledgementText.Text = command.SelectedIndex == 2
                ? "I understand that inhibit can disable the target subscriber."
                : "I confirm that this is the intended target subscriber.";
            UpdateAvailability();
        }
        void RefreshHistory()
        {
            var current = commands?.SubscriberCommandHistory ?? [];
            if (displayedAttempt is { } id && current.FirstOrDefault(entry => entry.Id == id) is { } result)
                status.Text = $"{result.SystemName} · RID {result.DestinationId}: {result.Detail}";
            if (ReferenceEquals(current, renderedHistory)) return;
            renderedHistory = current;
            history.Children.Clear();
            foreach (var item in current.Take(10))
                history.Children.Add(new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"{item.Timestamp.ToLocalTime():HH:mm:ss} · {item.SystemName} · {CommandNames[(int)item.Command]} RID {item.DestinationId}\n{item.Detail}"
                });
        }
        back.Click += (_, _) => { if (!sending) SettingsRequested?.Invoke(this, EventArgs.Empty); };
        command.SelectionChanged += (_, _) => ResetAcknowledgement();
        system.SelectionChanged += (_, _) => ResetAcknowledgement();
        destination.TextChanged += (_, _) => ResetAcknowledgement();
        acknowledgement.IsCheckedChanged += (_, _) => UpdateAvailability();
        send.Click += async (_, _) =>
        {
            if (sending || commands is null || system.SelectedIndex < 0 || command.SelectedIndex < 0) return;
            if (!uint.TryParse(destination.Text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out uint rid) || rid is 0 or > 0xFFFFFF)
            { status.Text = "Enter a P25 subscriber RID from 1 to 16777215."; return; }
            if (command.SelectedIndex >= 2 && acknowledgement.IsChecked != true) return;
            var target = targets[system.SelectedIndex];
            var selectedCommand = (ConsoleSubscriberCommand)command.SelectedIndex;
            displayedAttempt = null;
            sending = true; UpdateAvailability();
            system.IsEnabled = command.IsEnabled = destination.IsEnabled = acknowledgement.IsEnabled = false;
            try
            {
                var result = await commands.SendSubscriberCommandAsync(target.Id, selectedCommand, rid);
                displayedAttempt = result.Id;
                status.Text = $"{target.Name} · RID {rid}: {result.Detail}";
                RefreshHistory();
            }
            catch (Exception exception) { status.Text = exception.Message; }
            finally
            {
                sending = false;
                system.IsEnabled = command.IsEnabled = destination.IsEnabled = acknowledgement.IsEnabled = true;
                ResetAcknowledgement();
            }
        };
        UpdateAvailability(); RefreshHistory();
        Content = MobileSettingsPageLayout.Create(MobileSettingsPageLayout.Heading("Subscriber Commands", back), body);
    }
}
