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
using Xunit;

namespace DvmConsole.Desktop.RenderTests;

public sealed class MobileSubscriberCommandsTests
{
    [AvaloniaTheory]
    [InlineData(320)]
    [InlineData(768)]
    public async Task AcknowledgementUpdatesStatusAndHistoryWithoutRebuildingUnchangedRows(double width)
    {
        var commands = new Commands();
        var reference = new ConfigurationReference(ConfigurationId.New(), ConfigurationRevision.New());
        await using var application = new ConsoleApplicationSession(new(reference, [], [], []), ConsoleRuntimeSnapshot.Empty, commands);
        var view = new MobileSubscriberCommandsView(new(application));
        var window = new Window { Width = width, Height = 720, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            view.GetVisualDescendants().OfType<TextBox>()
                .Single(input => AutomationProperties.GetName(input) == "Destination subscriber RID").Text = "42";
            await DrainUi();
            var send = view.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "Send");
            var status = view.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "SubscriberCommandStatus");
            Assert.True(send.IsEnabled);
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await DrainUi();
            Assert.Contains("awaiting", status.Text);
            commands.Acknowledge();
            await DrainUi();
            Assert.Contains("Acknowledged by subscriber", status.Text);
            var row = view.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text?.Contains("\nAcknowledged by subscriber") == true);
            commands.PublishHistory();
            await DrainUi();
            Assert.Contains(row, view.GetVisualDescendants());
            Assert.True(send.Bounds.Height >= 44);
            Assert.InRange(send.Bounds.Right, 0, width);
            string? finalStatus = status.Text;
            commands.Acknowledge("Late detached notification");
            window.Close();
            await DrainUi();
            Assert.Equal(0, commands.ObserverCount);
            Assert.Equal(finalStatus, status.Text);
        }
        finally { window.Close(); }
    }

    private static async Task DrainUi()
        => await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

    private sealed class Commands : IConsoleCommands, IConsoleSubscriberCommands, IConsoleSubscriberHistoryNotifications
    {
        public IReadOnlyList<ConsoleSubscriberTarget> SubscriberTargets { get; } = [new(SystemId.FromName("Test"), "Test", true)];
        public ImmutableList<ConsoleSubscriberCommandResult> SubscriberCommandHistory { get; private set; } = [];
        public event EventHandler? SubscriberHistoryChanged;
        public int ObserverCount => SubscriberHistoryChanged?.GetInvocationList().Length ?? 0;
        public Task<ConsoleSubscriberCommandResult> SendSubscriberCommandAsync(SystemId system, ConsoleSubscriberCommand command,
            uint destinationId, CancellationToken cancellationToken = default)
        {
            var result = new ConsoleSubscriberCommandResult(DateTimeOffset.UnixEpoch, system, command, destinationId, true,
                "Sent; awaiting subscriber acknowledgement.")
            { SystemName = "Test" };
            SubscriberCommandHistory = SubscriberCommandHistory.Insert(0, result);
            PublishHistory();
            return Task.FromResult(result);
        }
        public void PublishHistory() => SubscriberHistoryChanged?.Invoke(this, EventArgs.Empty);
        public void Acknowledge(string detail = "Acknowledged by subscriber.")
        {
            SubscriberCommandHistory = SubscriberCommandHistory.SetItem(0, SubscriberCommandHistory[0] with
            { Detail = detail, Acknowledgement = ConsoleSubscriberAcknowledgementState.Received });
            PublishHistory();
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
