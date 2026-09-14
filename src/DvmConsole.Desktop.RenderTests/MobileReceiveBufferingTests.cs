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

public sealed class MobileReceiveBufferingTests
{
    [AvaloniaTheory]
    [InlineData(320)]
    [InlineData(768)]
    public async Task BufferingKeepsConnectionDraftsAndRejectsFailedOrStaleSaves(double width)
    {
        var commands = new Commands();
        var topology = new ConsoleTopologySnapshot(null, [new(First, "First", "DMR"), new(Second, "Second", "P25")], [], []);
        await using var application = new ConsoleApplicationSession(topology, ConsoleRuntimeSnapshot.Empty, commands);
        await using var replacement = new ConsoleApplicationSession(topology, ConsoleRuntimeSnapshot.Empty, commands);
        MobileSession current = new(application);
        var view = new MobileReceiveBufferingView(() => current);
        var window = new Window { Width = width, Height = 800, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            ComboBox Choice(string label) => view.GetVisualDescendants().OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == label);
            var systems = Choice("Receive Buffering connection");
            var adaptive = view.GetVisualDescendants().OfType<CheckBox>().Single(c => AutomationProperties.GetName(c) == "DMR adaptive buffering");
            Assert.False(Choice("DMR fixed delay").IsEnabled);
            adaptive.IsChecked = false;
            Assert.True(Choice("DMR fixed delay").IsEnabled);
            Choice("DMR fixed delay").SelectedItem = 240;
            systems.SelectedIndex = 1;
            window.UpdateLayout();
            Assert.Equal(120, Choice("DMR fixed delay").SelectedItem);
            systems.SelectedIndex = 0;
            window.UpdateLayout();
            Assert.Equal(240, Choice("DMR fixed delay").SelectedItem);
            Assert.All(view.GetVisualDescendants().OfType<ComboBox>(), c =>
            { Assert.InRange(c.Bounds.Width, 100, width - 32); Assert.True(c.Bounds.Height >= 48); });
            var apply = view.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Apply receive buffering");
            async Task Apply()
            {
                apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            }
            await Apply();
            Assert.Equal(240, commands.ReceiveBuffering[First].DmrMilliseconds);
            Assert.False(commands.ReceiveBuffering[First].DmrAdaptive);
            Assert.True(commands.ReceiveBuffering[Second].DmrAdaptive);
            commands.Fail = true;
            Choice("DMR fixed delay").SelectedItem = 300;
            await Apply();
            Assert.Equal(300, Choice("DMR fixed delay").SelectedItem);
            Assert.Equal(240, commands.ReceiveBuffering[First].DmrMilliseconds);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Storage unavailable");
            current = new(replacement);
            await Apply();
            Assert.Equal(2, commands.Saves);
        }
        finally { window.Close(); }
    }

    private static readonly SystemId First = SystemId.FromName("First");
    private static readonly SystemId Second = SystemId.FromName("Second");
    private sealed class Commands : IConsoleCommands, IConsoleReceiveBufferingSettings
    {
        public ImmutableDictionary<SystemId, ConsoleReceiveBufferingOptions> ReceiveBuffering { get; private set; }
            = ImmutableDictionary<SystemId, ConsoleReceiveBufferingOptions>.Empty.Add(First, new()).Add(Second, new());
        public bool CanSaveReceiveBuffering => true;
        public bool Fail { get; set; }
        public int Saves { get; private set; }
        public ValueTask SetReceiveBufferingAsync(SystemId system, ConsoleReceiveBufferingOptions options, CancellationToken cancellationToken = default)
        {
            Saves++;
            if (Fail) return ValueTask.FromException(new IOException("Storage unavailable"));
            ReceiveBuffering = ReceiveBuffering.SetItem(system, options);
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
