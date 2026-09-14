// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;

namespace DvmConsole.Mobile;

/// <summary>A scrollable, host-owned confirmation used by mobile Studio.</summary>
public sealed class MobileConfirmationPrompt : ContentControl
{
    private readonly Button cancel = new() { Content = "Cancel", MinHeight = 44 };

    public void FocusCancel() => cancel.Focus();

    protected override Type StyleKeyOverride => typeof(ContentControl);

    public MobileConfirmationPrompt(string title, string message, string action, Action<bool> complete)
    {
        ArgumentNullException.ThrowIfNull(complete);
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 12 };
        var heading = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold };
        var explanation = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(heading, title);
        AutomationProperties.SetName(explanation, message);
        body.Children.Add(heading);
        var scroll = new ScrollViewer { Content = explanation };
        Grid.SetRow(scroll, 1);
        body.Children.Add(scroll);
        var actions = new WrapPanel();
        var accept = new Button { Content = action, MinHeight = 44, Margin = new Thickness(0, 0, 8, 0) };
        accept.Click += (_, _) => complete(true);
        cancel.Click += (_, _) => complete(false);
        actions.Children.Add(accept);
        actions.Children.Add(cancel);
        Grid.SetRow(actions, 2);
        body.Children.Add(actions);
        Content = body;
    }
}
