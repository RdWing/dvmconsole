// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using DvmConsole.Application;

namespace DvmConsole.Mobile;

/// <summary>Edits next-call audio policy without changing an active transmission.</summary>
internal sealed class MobileManualTransmitSettingsView : UserControl
{
    private readonly Func<MobileSession> session;
    private readonly ToggleSwitch permit = Choice("Talk-permit tone");
    private readonly ToggleSwitch mute = Choice("Mute RX audio while transmitting");
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private bool saving;
    private bool updating;

    public MobileManualTransmitSettingsView(Func<MobileSession> session)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(permit);
        body.Children.Add(mute);
        body.Children.Add(new TextBlock { Text = "Saved on this device. Changes apply to the next manual PTT call.", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(status);
        Content = body;
        AttachedToVisualTree += (_, _) => Refresh();
        permit.IsCheckedChanged += async (_, _) => await SaveAsync();
        mute.IsCheckedChanged += async (_, _) => await SaveAsync();
    }

    private static ToggleSwitch Choice(string label)
    {
        var choice = new ToggleSwitch { MinHeight = 48, Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap } };
        AutomationProperties.SetName(choice, label);
        return choice;
    }

    private void Refresh()
    {
        updating = true;
        var settings = session().Application.Commands as IConsoleManualTransmitSettings;
        IsVisible = settings?.CanSaveManualTransmitOptions == true;
        permit.IsChecked = settings?.ManualTransmitOptions.TalkPermitTone == true;
        mute.IsChecked = settings?.ManualTransmitOptions.MuteReceiveWhileTransmitting == true;
        permit.IsEnabled = mute.IsEnabled = !saving;
        updating = false;
    }

    private async Task SaveAsync()
    {
        var current = session().Application;
        if (updating || saving || current.Commands is not IConsoleManualTransmitSettings settings || !settings.CanSaveManualTransmitOptions) return;
        saving = true;
        permit.IsEnabled = mute.IsEnabled = false;
        status.IsVisible = false;
        try { await settings.SetManualTransmitOptionsAsync(new(permit.IsChecked == true, mute.IsChecked == true)); }
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
