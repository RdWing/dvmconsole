// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DvmConsole.Application;

namespace DvmConsole.Mobile;

/// <summary>Touch connection controls observe shared transport state and issue shared commands.</summary>
internal sealed class MobileConnectionsView : UserControl
{
    private readonly Func<MobileSession> session;
    private readonly StackPanel systems = new() { Spacing = 12 };
    private readonly TextBlock placeholder = new() { Text = "Open a configuration to connect systems.", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button connect = new() { Content = "Connect systems", MinHeight = 48 };
    private readonly Button disconnect = new() { Content = "Disconnect systems", MinHeight = 48 };
    private readonly Dictionary<SystemId, ConnectionRow> rows = [];
    private IConsoleApplicationSession? displayedSession;
    private bool busy;
    private bool refreshingChimes;
    private bool savingChimes;
    private readonly ToggleSwitch chimes = new() { Content = "Connection chimes", MinHeight = 48 };

    public MobileConnectionsView(Func<MobileSession> session)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(placeholder);
        body.Children.Add(systems);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8 };
        actions.Children.Add(connect);
        actions.Children.Add(disconnect);
        body.Children.Add(actions);
        body.Children.Add(chimes);
        AutomationProperties.SetName(chimes, "Connection chimes");
        chimes.IsCheckedChanged += async (_, _) => await SaveChimesAsync();
        body.Children.Add(status);
        Content = body;
        connect.Click += async (_, _) => await RunAsync(commands => commands.ConnectAsync());
        disconnect.Click += async (_, _) => await RunAsync(commands => commands.DisconnectAsync());
        var refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        refresh.Tick += (_, _) => Refresh();
        AttachedToVisualTree += (_, _) => { Refresh(); refresh.Start(); };
        DetachedFromVisualTree += (_, _) => refresh.Stop();
    }

    internal Task ToggleAsync(SystemId id) => RunAsync(commands => commands.ToggleAsync(id));

    internal void Refresh()
    {
        MobileSession current = session();
        var states = (current.Connections as IConsoleConnectionStateSource)?.ConnectionStates ?? [];
        if (states.IsDefault) states = [];
        if (!ReferenceEquals(displayedSession, current.Application) || rows.Count != states.Length ||
            states.Any(state => !rows.ContainsKey(state.SystemId)))
        {
            displayedSession = current.Application;
            ShowStatus(string.Empty);
            rows.Clear();
            systems.Children.Clear();
            foreach (RadioConnectionSnapshot state in states)
            {
                var row = new ConnectionRow();
                row.Toggle.Click += async (_, _) =>
                {
                    if (ReferenceEquals(current.Application, session().Application)) await ToggleAsync(state.SystemId);
                    else Refresh();
                };
                rows.Add(state.SystemId, row);
                systems.Children.Add(row.View);
            }
        }
        IReadOnlyList<SystemId>? active = current.ActiveSystems?.Invoke();
        foreach (RadioConnectionSnapshot state in states)
        {
            bool connected = active?.Contains(state.SystemId) ?? state.State is not
                (RadioConnectionState.Disconnected or RadioConnectionState.Faulted);
            rows[state.SystemId].Update(state, connected, busy);
        }
        var cueSettings = current.Application.Commands as IConsoleConnectionCueSettings;
        refreshingChimes = true;
        try
        {
            chimes.IsVisible = cueSettings is not null;
            chimes.IsEnabled = !savingChimes && cueSettings?.CanSaveConnectionChimes == true;
            if (!savingChimes) chimes.IsChecked = cueSettings?.ConnectionChimes == true;
        }
        finally { refreshingChimes = false; }
        placeholder.IsVisible = states.IsEmpty;
        connect.IsEnabled = disconnect.IsEnabled = !busy && current.Connections is not null;
    }

    private async Task SaveChimesAsync()
    {
        if (refreshingChimes || savingChimes) return;
        var owner = session().Application;
        if (!ReferenceEquals(owner, displayedSession)) { Refresh(); return; }
        if (owner.Commands is not IConsoleConnectionCueSettings { CanSaveConnectionChimes: true } settings) return;
        savingChimes = true;
        chimes.IsEnabled = false;
        try { await settings.SetConnectionChimesAsync(chimes.IsChecked == true); }
        catch (Exception exception)
        {
            if (ReferenceEquals(owner, session().Application)) ShowStatus(exception.Message);
        }
        finally { savingChimes = false; Refresh(); }
    }

    private async Task RunAsync(Func<IConsoleConnectionCommands, Task> operation)
    {
        MobileSession current = session();
        if (busy || current.Connections is not { } commands) return;
        busy = true;
        Refresh();
        try
        {
            await operation(commands);
            if (ReferenceEquals(current.Application, session().Application))
                ShowStatus(current.Application.Snapshot.StatusText);
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(current.Application, session().Application)) ShowStatus(exception.Message);
        }
        finally { busy = false; Refresh(); }
    }

    private void ShowStatus(string message)
    {
        status.Text = message;
        status.IsVisible = !string.IsNullOrWhiteSpace(message);
    }

    private sealed class ConnectionRow
    {
        private readonly TextBlock name = new() { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock detail = new() { TextWrapping = TextWrapping.Wrap };
        private RadioConnectionSnapshot? previous;
        private bool? previouslyConnected;
        public Button Toggle { get; } = new() { MinHeight = 48, MinWidth = 104, VerticalAlignment = VerticalAlignment.Top };
        public Grid View { get; } = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };

        public ConnectionRow()
        {
            var text = new StackPanel { Spacing = 4 };
            text.Children.Add(name);
            text.Children.Add(detail);
            View.Children.Add(text);
            Grid.SetColumn(Toggle, 1);
            View.Children.Add(Toggle);
        }

        public void Update(RadioConnectionSnapshot state, bool connected, bool busy)
        {
            if (previous != state || previouslyConnected != connected)
            {
                name.Text = state.Name;
                detail.Text = $"{state.State} — {state.Message}";
                string action = connected ? "Disconnect" : "Connect";
                Toggle.Content = action;
                AutomationProperties.SetName(Toggle, $"{action} {state.Name}");
                previous = state;
                previouslyConnected = connected;
            }
            Toggle.IsEnabled = !busy && state.State != RadioConnectionState.Stopping;
        }
    }
}
