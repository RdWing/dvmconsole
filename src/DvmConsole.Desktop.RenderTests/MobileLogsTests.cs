// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DvmConsole.Application;
using DvmConsole.Mobile;
using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.RenderTests;

public sealed class MobileLogsTests
{
    [AvaloniaTheory]
    [InlineData(390, 844)]
    [InlineData(852, 390)]
    [InlineData(1024, 768)]
    public async Task LogsRetainAcrossNavigationFilterAndDetachFromRetiredSession(double width, double height)
    {
        await using var console = new MobileConsoleView(ConsoleHostFormFactor.Phone);
        var commands = new DiagnosticCommands();
        await using var application = new ConsoleApplicationSession(console.ApplicationSession.Topology,
            console.ApplicationSession.Snapshot, commands);
        using var diagnostics = new MobileSessionDiagnostics(application);
        var session = new MobileSession(application) { Diagnostics = diagnostics.Workspace };
        var logs = new MobileLogsView(() => session);
        var window = new Window { Width = width, Height = height, Content = logs };
        try
        {
            window.Show();
            window.UpdateLayout();
            var verbose = Assert.Single(logs.GetVisualDescendants().OfType<CheckBox>());
            Assert.True(verbose.IsVisible);
            verbose.IsChecked = true;
            Assert.True(commands.VerboseLoggingEnabled);
            commands.Fail = true;
            verbose.IsChecked = false;
            Assert.True(verbose.IsChecked);
            Assert.Contains(logs.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Settings unavailable");
            application.PublishLog(new(DateTimeOffset.UtcNow, ConsoleLogLevel.Information, "Receive", "Decoded call"));
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            ListBox entries = Assert.Single(logs.GetVisualDescendants().OfType<ListBox>());
            Assert.Single(entries.Items);
            foreach (Button button in logs.GetVisualDescendants().OfType<Button>())
                Assert.True(button.Bounds.Height >= 44);
            Assert.True(entries.Bounds.Height > 100);
            ComboBox severity = Assert.Single(logs.GetVisualDescendants().OfType<ComboBox>());
            severity.SelectedItem = "Error";
            Assert.Empty(entries.Items);
            severity.SelectedItem = "All";
            Assert.Single(entries.Items);
            window.Content = new Border();
            application.PublishLog(new(DateTimeOffset.UtcNow, ConsoleLogLevel.Warning, "Receive", "Queue pressure"));
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            window.Content = logs;
            window.UpdateLayout();
            Assert.Equal(2, entries.Items.Count);
            using var export = new MemoryStream();
            Assert.Equal(2, diagnostics.Workspace.Export(export));
            Assert.Contains("Queue pressure", System.Text.Encoding.UTF8.GetString(export.ToArray()), StringComparison.Ordinal);
            diagnostics.Dispose();
            application.PublishLog(new(DateTimeOffset.UtcNow, ConsoleLogLevel.Error, "Retired", "Must not append"));
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            Assert.Equal(2, diagnostics.Workspace.Entries.Count);
        }
        finally { window.Close(); }
    }
    private sealed class DiagnosticCommands : IConsoleCommands, IConsoleDiagnosticSettings
    {
        public bool VerboseLoggingEnabled { get; private set; }
        public bool CanSaveDiagnosticSettings => true;
        public bool Fail { get; set; }
        public ValueTask SetVerboseLoggingAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new IOException("Settings unavailable");
            VerboseLoggingEnabled = enabled;
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
