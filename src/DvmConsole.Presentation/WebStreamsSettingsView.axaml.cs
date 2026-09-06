// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DvmConsole.Presentation;

public sealed partial class WebStreamsSettingsView : UserControl
{
    public WebStreamsSettingsView()
    {
        InitializeComponent();
        ResponsiveSettingsDensity.Attach(this);
    }

    public event EventHandler<WebStreamRouteSaveEventArgs>? SaveRouteRequested;

    private void HandleSaveRouteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: IWebStreamViewModel stream })
            SaveRouteRequested?.Invoke(this, new WebStreamRouteSaveEventArgs(stream));
    }
}
