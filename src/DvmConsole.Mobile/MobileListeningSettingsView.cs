// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using Avalonia.Media;
using DvmConsole.Application;

namespace DvmConsole.Mobile;

/// <summary>Edits next-launch listening intent without changing the running session.</summary>
internal sealed class MobileListeningSettingsView : UserControl
{
    private readonly ToggleSwitch restore = new()
    {
        Content = new TextBlock
        { Text = "Restore selected channels and streams on startup", TextWrapping = TextWrapping.Wrap },
        MinHeight = 48
    };
    private readonly ToggleSwitch autoConnect = new() { Content = "Auto-connect FNEs on startup", MinHeight = 48 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Func<MobileSession> session;
    private bool updating;
    private bool saving;

    public MobileListeningSettingsView(Func<MobileSession> session)
    {
        this.session = session;
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(restore);
        body.Children.Add(autoConnect);
        body.Children.Add(status);
        Content = body;
        AttachedToVisualTree += async (_, _) =>
        {
            Refresh();
            var current = session();
            autoConnect.IsVisible = current.ConnectionStartup is not null;
            autoConnect.IsEnabled = false;
            try
            {
                if (current.ConnectionStartup is { } preferences)
                {
                    bool enabled = await preferences.LoadAutoConnectAsync();
                    if (!ReferenceEquals(current.Application, session().Application)) return;
                    updating = true;
                    autoConnect.IsChecked = enabled;
                }
            }
            catch (Exception exception) { status.Text = exception.Message; status.IsVisible = true; }
            finally { updating = false; autoConnect.IsEnabled = true; }
        };
        autoConnect.IsCheckedChanged += async (_, _) =>
        {
            if (updating || !autoConnect.IsEnabled || session().ConnectionStartup is not { } preferences) return;
            autoConnect.IsEnabled = false;
            try { await preferences.SaveAutoConnectAsync(autoConnect.IsChecked == true); }
            catch (Exception exception)
            {
                updating = true;
                autoConnect.IsChecked = autoConnect.IsChecked != true;
                updating = false;
                status.Text = exception.Message; status.IsVisible = true;
            }
            finally { autoConnect.IsEnabled = true; }
        };
        restore.IsCheckedChanged += async (_, _) =>
        {
            if (!updating && !saving) await SaveAsync(restore.IsChecked == true);
        };
    }

    private void Refresh()
    {
        updating = true;
        try
        {
            var settings = session().Application.Commands as IConsoleListeningSettings;
            IsVisible = settings?.CanSaveStartupPreference == true;
            restore.IsChecked = settings?.RestoreSelectedChannelsOnStartup == true;
            restore.IsEnabled = !saving;
        }
        finally { updating = false; }
    }

    internal async Task SaveAsync(bool enabled)
    {
        var current = session().Application;
        if (saving || current.Commands is not IConsoleListeningSettings settings || !settings.CanSaveStartupPreference) return;
        saving = true;
        restore.IsEnabled = false;
        status.IsVisible = false;
        try { await settings.SetRestoreSelectedChannelsAsync(enabled); }
        catch (Exception exception)
        {
            if (ReferenceEquals(current, session().Application))
            {
                status.Text = exception.Message;
                status.IsVisible = true;
            }
        }
        finally { saving = false; Refresh(); }
    }
}
