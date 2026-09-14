// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DvmConsole.Application;

namespace DvmConsole.Mobile;

internal sealed class MobileGroupView : UserControl
{
    public event EventHandler? SettingsRequested;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel editor = new() { Spacing = 8 };
    private readonly IConsoleGroupSettings? groups;
    private readonly IConsoleApplicationSession session;
    private bool busy;
    private readonly Func<bool>? isCurrent;

    public MobileGroupView(MobileSession current, bool embedded = false, Func<bool>? isCurrent = null)
    {
        this.isCurrent = isCurrent;
        session = current.Application;
        groups = session.Commands as IConsoleGroupSettings;
        var back = new Button { Content = "‹ Settings", MinHeight = 44 };
        back.Click += (_, _) => { if (!busy) SettingsRequested?.Invoke(this, EventArgs.Empty); };
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(status);
        body.Children.Add(editor);
        Content = embedded ? body : MobileSettingsPageLayout.Create(MobileSettingsPageLayout.Heading("Groups", back), body);
        ShowGroups();
    }

    private void ShowGroups()
    {
        editor.Children.Clear();
        if (groups is null) { status.Text = "Open a configuration to edit groups."; return; }
        var restore = new ToggleSwitch
        {
            Content = new TextBlock { Text = "Restore enabled patches on startup", TextWrapping = TextWrapping.Wrap },
            MinHeight = 44,
            IsChecked = groups.RestorePatchesOnStartup
        };
        restore.IsCheckedChanged += async (_, _) =>
        {
            if (busy || restore.IsChecked == groups.RestorePatchesOnStartup) return;
            await RunAsync(() => groups.SetRestorePatchesAsync(restore.IsChecked == true));
            if (restore.IsChecked != groups.RestorePatchesOnStartup) restore.IsChecked = groups.RestorePatchesOnStartup;
        };
        editor.Children.Add(restore);
        if (groups.SavedGroups.Count == 0)
        {
            editor.Children.Add(new TextBlock { Text = "Add patch or multi-select groups in Configuration Studio.", TextWrapping = TextWrapping.Wrap });
            return;
        }
        var names = new ComboBox
        {
            ItemsSource = groups.SavedGroups.Select(group => group.Name).ToArray(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 44
        };
        var details = new StackPanel { Spacing = 8 };
        names.SelectionChanged += (_, _) =>
        {
            details.Children.Clear();
            if (names.SelectedItem is string name)
                ShowGroup(details, groups.SavedGroups.First(group => group.Name == name));
        };
        editor.Children.Add(names);
        editor.Children.Add(details);
        names.SelectedIndex = 0;
    }

    private void ShowGroup(StackPanel details, ConsoleGroupDefinitionSnapshot group)
    {
        var members = group.Members.ToList();
        var enabled = new ToggleSwitch
        {
            Content = "Enable patch",
            MinHeight = 44,
            IsVisible = !group.IsMultiSelect,
            IsChecked = groups!.EnabledPatchGroups.Contains(group.Name)
        };
        var oneWay = new ToggleSwitch
        {
            Content = "One-way patch",
            MinHeight = 44,
            IsVisible = !group.IsMultiSelect,
            IsChecked = group.OneWay
        };
        details.Children.Add(enabled); details.Children.Add(oneWay);
        if (group.UnresolvedMembers > 0)
            details.Children.Add(new TextBlock { Text = $"{group.UnresolvedMembers} saved member(s) are unavailable. Review all members before applying.", TextWrapping = TextWrapping.Wrap });
        var channels = session.Topology.Channels.ToArray();
        var source = new ComboBox
        {
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = channels.Select(channel => $"{channel.SystemId.Value} · {channel.Name}").ToArray(),
            IsVisible = !group.IsMultiSelect && group.OneWay,
            SelectedIndex = Array.FindIndex(channels, channel => members.FirstOrDefault() == channel.Id)
        };
        var sourceLabel = new TextBlock { Text = "Source for one-way forwarding", IsVisible = source.IsVisible };
        oneWay.IsCheckedChanged += (_, _) => sourceLabel.IsVisible = source.IsVisible = oneWay.IsChecked == true;
        details.Children.Add(sourceLabel);
        details.Children.Add(source);
        Control ChannelChoices(IEnumerable<ChannelDescriptor> entries)
        {
            var choices = new StackPanel { Spacing = 4 };
            foreach (var channel in entries)
            {
                var choice = new CheckBox
                {
                    Content = new TextBlock
                    {
                        Text = channel.Name,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    MinHeight = 44,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    IsChecked = members.Contains(channel.Id)
                };
                choice.IsCheckedChanged += (_, _) =>
                {
                    if (choice.IsChecked == true) { if (!members.Contains(channel.Id)) members.Add(channel.Id); }
                    else members.Remove(channel.Id);
                };
                choices.Children.Add(choice);
            }
            return choices;
        }
        // Build membership controls only when opened; collapsing retains edits and control identity.
        Expander Disclosure(string title, Func<Control> create)
        {
            var disclosure = new Expander { Header = title, HorizontalAlignment = HorizontalAlignment.Stretch };
            disclosure.Expanding += (_, _) => disclosure.Content ??= create();
            return disclosure;
        }
        foreach (var system in channels.GroupBy(channel => channel.SystemId))
        {
            string name = session.Topology.Systems.FirstOrDefault(item => item.Id == system.Key)?.Name ?? system.Key.Value;
            details.Children.Add(Disclosure(name, () =>
            {
                var zones = system.GroupBy(channel => channel.ZoneId).ToArray();
                if (zones.Length == 1) return ChannelChoices(system);
                var zoneList = new StackPanel { Spacing = 4, Margin = new Thickness(12, 0, 0, 0) };
                foreach (var zone in zones)
                {
                    string zoneName = session.Topology.Zones.FirstOrDefault(item => item.Id == zone.Key)?.Name ?? "Zone";
                    zoneList.Children.Add(Disclosure(zoneName, () => ChannelChoices(zone)));
                }
                return zoneList;
            }));
        }
        var save = new Button { Content = group.IsMultiSelect ? "Save group" : "Save and apply", MinHeight = 44 };
        save.Click += async (_, _) => await RunAsync(async () =>
        {
            var ordered = members.ToList();
            if (!group.IsMultiSelect && oneWay.IsChecked == true)
            {
                if (source.SelectedIndex < 0 || !ordered.Contains(channels[source.SelectedIndex].Id))
                    throw new InvalidOperationException("Select a source that is also a group member.");
                var first = channels[source.SelectedIndex].Id;
                ordered.Remove(first); ordered.Insert(0, first);
            }
            await groups.SaveGroupAsync(group.Name, ordered, enabled.IsChecked == true, oneWay.IsChecked == true);
        });
        details.Children.Add(save);
        if (group.IsMultiSelect && session.Commands is IConsoleGroupSelectionCommands selection)
        {
            details.Children.Add(new TextBlock
            {
                Text = "Add the saved members to TX selection, then use TX selected on the console. Other selected channels stay selected.",
                TextWrapping = TextWrapping.Wrap
            });
            var select = new Button { Content = "Add saved group to TX selection", MinHeight = 44 };
            select.Click += async (_, _) => await RunAsync(async () =>
            {
                int count = await selection.AddGroupToTransmitSelectionAsync(group.Name);
                return $"{count} group channel(s) selected. Return to Console to transmit.";
            });
            details.Children.Add(select);
        }
    }

    private async Task RunAsync(Func<Task> action)
        => await RunAsync(async () => { await action(); return "Group settings saved."; });

    private async Task RunAsync(Func<Task<string>> action)
    {
        if (busy) return;
        busy = true; editor.IsEnabled = false;
        try
        {
            if (isCurrent?.Invoke() == false)
                throw new InvalidOperationException("The active configuration changed. Reopen Groups before applying changes.");
            status.Text = await action();
        }
        catch (Exception exception) { status.Text = exception.Message; }
        finally { busy = false; editor.IsEnabled = true; }
    }
}
