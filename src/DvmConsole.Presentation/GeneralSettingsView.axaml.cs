// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DvmConsole.Presentation;

public sealed partial class GeneralSettingsView : UserControl
{
    public GeneralSettingsView()
    {
        InitializeComponent();
        ResponsiveSettingsDensity.Attach(this);
    }

    public event EventHandler? ResetLayoutRequested;
    public event EventHandler? SaveToolbarClocksRequested;

    public void BringClockSettingsIntoView() => ClockSettingsSection.BringIntoView();

    private void HandleResetLayoutClick(object? sender, RoutedEventArgs e)
        => ResetLayoutRequested?.Invoke(this, EventArgs.Empty);
    private void HandleSaveToolbarClocksClick(object? sender, RoutedEventArgs e)
        => SaveToolbarClocksRequested?.Invoke(this, EventArgs.Empty);
}
