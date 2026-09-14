// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DvmConsole.Core.Configuration;

namespace DvmConsole.Presentation;

public sealed partial class ConfigurationStudioFilesView : UserControl
{
    public ConfigurationStudioFilesView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyTouchLayout();
    }

    private bool touchLayout;
    private bool compactLayout;

    internal void SetTouchLayout(bool enabled)
    {
        touchLayout = enabled;
        ApplyTouchLayout();
    }

    private void ApplyTouchLayout()
    {
        bool compact = touchLayout && Bounds.Width < 800;
        if (compact == compactLayout) return;
        compactLayout = compact;
        var header = this.FindControl<Grid>("AliasHeader")!;
        header.ColumnDefinitions = new(compact ? "*" : "*,220,Auto");
        header.RowDefinitions = new(compact ? "Auto,Auto,Auto" : "*");
        header.RowSpacing = compact ? 8 : 0;
        var owner = this.FindControl<ComboBox>("AliasOwner")!;
        var actions = this.FindControl<WrapPanel>("AliasActions")!;
        Grid.SetColumn(owner, compact ? 0 : 1);
        Grid.SetRow(owner, compact ? 1 : 0);
        Grid.SetColumn(actions, compact ? 0 : 2);
        Grid.SetRow(actions, compact ? 2 : 0);
        var fields = this.FindControl<Grid>("AliasFields")!;
        fields.ColumnDefinitions = new(compact ? "*" : "*,2*");
        fields.RowDefinitions = new(compact ? "Auto,Auto" : "*");
        fields.RowSpacing = compact ? 8 : 0;
        Control name = fields.Children[1];
        Grid.SetColumn(name, compact ? 0 : 1);
        Grid.SetRow(name, compact ? 1 : 0);
    }

    public event EventHandler? DeleteAliasRequested;
    public event EventHandler? BrowseKeyFileRequested;
    public event EventHandler<ConfigurationStudioAliasFileEventArgs>? BrowseAliasFileRequested;
    public event EventHandler? ExportFullRequested;
    public event EventHandler? ExportSanitizedRequested;

    private ConfigurationStudioViewModel? ViewModel => DataContext as ConfigurationStudioViewModel;

    private void HandleDraftFieldEdit(object? sender, RoutedEventArgs e) => ViewModel?.CommitFieldEdit();
    private void HandleAliasFieldEdit(object? sender, RoutedEventArgs e) => ViewModel?.CommitAliasEdit();
    private void HandleAddAliasClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.AddAlias();
        Dispatcher.UIThread.Post(() =>
        {
            if (this.FindControl<TextBox>("AliasRidEditor") is { } ridEditor)
                ridEditor.Focus();
        }, DispatcherPriority.Input);
    }
    private void HandleDeleteAliasClick(object? sender, RoutedEventArgs e)
        => DeleteAliasRequested?.Invoke(this, EventArgs.Empty);
    private void HandleBrowseKeyFileClick(object? sender, RoutedEventArgs e)
        => BrowseKeyFileRequested?.Invoke(this, EventArgs.Empty);
    private void HandleBrowseAliasFileClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SystemConfiguration system })
            BrowseAliasFileRequested?.Invoke(this, new ConfigurationStudioAliasFileEventArgs(system));
    }
    private void HandleExportFullClick(object? sender, RoutedEventArgs e)
        => ExportFullRequested?.Invoke(this, EventArgs.Empty);
    private void HandleExportSanitizedClick(object? sender, RoutedEventArgs e)
        => ExportSanitizedRequested?.Invoke(this, EventArgs.Empty);
}

public sealed class ConfigurationStudioAliasFileEventArgs(SystemConfiguration system) : EventArgs
{
    public SystemConfiguration System { get; } = system ?? throw new ArgumentNullException(nameof(system));
}
