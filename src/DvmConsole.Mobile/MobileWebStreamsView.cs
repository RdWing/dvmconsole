// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DvmConsole.Application;

namespace DvmConsole.Mobile;

internal sealed class MobileWebStreamsView : UserControl
{
    private readonly IConsoleWebStreamCommands? commands;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<WebStreamId, Row> rows = [];
    private bool attached;
    public event EventHandler? SettingsRequested;

    public MobileWebStreamsView(MobileSession session)
    {
        commands = session.Application.Commands as IConsoleWebStreamCommands;
        var back = new Button { Content = "‹ Settings", MinHeight = 44 };
        back.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        var body = new StackPanel
        {
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        body.Children.Add(status);
        foreach (var stream in commands?.WebStreams ?? [])
        {
            var row = new Row(stream, commands!, ReportAsync);
            rows.Add(stream.Id, row);
            body.Children.Add(row.Content);
        }
        if (rows.Count == 0) status.Text = "Add web streams to a zone in Configuration Studio.";
        Content = MobileSettingsPageLayout.Create(MobileSettingsPageLayout.Heading("Web streams", back), body);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        attached = true;
        if (commands is not null) commands.WebStreamsChanged += Changed;
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        attached = false;
        if (commands is not null) commands.WebStreamsChanged -= Changed;
        base.OnDetachedFromVisualTree(e);
    }

    private void Changed(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() => { if (attached) Refresh(); });

    private void Refresh()
    {
        foreach (var stream in commands?.WebStreams ?? [])
            if (rows.TryGetValue(stream.Id, out var row)) row.Refresh(stream);
    }

    private async Task ReportAsync(Func<Task> action)
    {
        try { await action(); status.Text = string.Empty; }
        catch (OperationCanceledException) { /* A newer stop or interruption owns the state. */ }
        catch (Exception exception) { status.Text = exception.Message; }
    }

    private sealed class Row
    {
        private ConsoleWebStreamSnapshot current;
        private readonly Button play = new() { MinHeight = 44, MinWidth = 80 };
        private readonly TextBlock state = new() { TextWrapping = TextWrapping.Wrap };
        private readonly Slider volume;
        private double requestedVolume;
        public Control Content { get; }

        public Row(ConsoleWebStreamSnapshot stream, IConsoleWebStreamCommands commands, Func<Func<Task>, Task> report)
        {
            current = stream;
            requestedVolume = stream.Volume;
            volume = new Slider { Minimum = 0, Maximum = 4, Value = stream.Volume, MinHeight = 44 };
            AutomationProperties.SetName(volume, $"{stream.Name} volume");
            var panel = new StackPanel { Spacing = 6 };
            var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
            heading.Children.Add(new TextBlock
            {
                Text = stream.Name,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(play, 1);
            heading.Children.Add(play);
            panel.Children.Add(heading); panel.Children.Add(state); panel.Children.Add(volume);
            Content = new Border
            {
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = Brush.Parse("#2A3A48"),
                Child = panel
            };
            play.Click += async (_, _) => await report(() => commands.SetWebStreamPlayingAsync(stream.Id, !current.Selected));
            async Task CommitVolumeAsync()
            {
                double selected = volume.Value;
                if (selected == requestedVolume) return;
                requestedVolume = selected;
                await report(async () =>
                {
                    try { await commands.SetWebStreamVolumeAsync(stream.Id, selected); }
                    catch { requestedVolume = double.NaN; throw; }
                });
            }
            // Persist the completed adjustment, not every pointer movement.
            volume.AddHandler(InputElement.PointerReleasedEvent, async (_, _) => await CommitVolumeAsync(), RoutingStrategies.Bubble, true);
            volume.KeyUp += async (_, _) => await CommitVolumeAsync();
            volume.LostFocus += async (_, _) => await CommitVolumeAsync();
            Refresh(stream);
        }

        public void Refresh(ConsoleWebStreamSnapshot stream)
        {
            current = stream;
            play.Content = stream.Selected ? "Stop" : "Play";
            state.Text = stream.Selected && stream.Playback.Status == "Off" ? "Waiting for listening to resume" : stream.Playback.Status;
            if (!volume.IsFocused) volume.Value = stream.Volume;
        }
    }
}
