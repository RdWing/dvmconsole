// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DvmConsole.Core.Configuration;

namespace DvmConsole.Presentation;

public sealed partial class ConfigurationStudioKeysView : UserControl
{
    private int queuedCommitVersion;
    private bool handlingSelectionChange;

    public ConfigurationStudioKeysView()
    {
        InitializeComponent();
        desktopKeyTemplate = this.FindControl<ListBox>("KeyList")!.ItemTemplate;
        SizeChanged += (_, _) => ApplyTouchLayout();
    }

    private readonly IDataTemplate? desktopKeyTemplate;
    private bool touchLayout;
    private bool compactLayout;
    private ScrollViewer? compactScroll;

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
        var root = this.FindControl<Grid>("KeysRoot")!;
        var columns = this.FindControl<Grid>("KeyColumns")!;
        var listPanel = this.FindControl<Border>("KeyListPanel")!;
        var list = this.FindControl<ListBox>("KeyList")!;
        var inspector = this.FindControl<Border>("KeyInspector")!;
        root.RowDefinitions = new(compact ? "Auto,Auto" : "Auto,*");
        columns.ColumnDefinitions = new(compact ? "*" : "3*,2*");
        columns.RowDefinitions = new(compact ? "Auto,Auto" : "*");
        columns.RowSpacing = compact ? 12 : 0;
        Grid.SetColumn(inspector, compact ? 0 : 1);
        Grid.SetRow(inspector, compact ? 1 : 0);
        listPanel.MaxHeight = compact ? 180 : double.PositiveInfinity;
        listPanel.MinHeight = compact ? 80 : 0;
        this.FindControl<Border>("KeyTableHeader")!.IsVisible = !compact;
        list.ItemTemplate = compact ? (IDataTemplate)Resources["CompactKeyTemplate"]! : desktopKeyTemplate;
        if (compact)
        {
            Content = null;
            compactScroll = new ScrollViewer
            {
                Content = root,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            };
            Content = compactScroll;
        }
        else
        {
            compactScroll!.Content = null;
            Content = root;
            compactScroll = null;
        }
    }

    public event EventHandler? DeleteRequested;

    private ConfigurationStudioViewModel? ViewModel => DataContext as ConfigurationStudioViewModel;

    private void HandleAddKeyClick(object? sender, RoutedEventArgs e) => ViewModel?.AddKey();
    private void HandleDeleteKeyClick(object? sender, RoutedEventArgs e)
        => DeleteRequested?.Invoke(this, EventArgs.Empty);
    private void HandleKeyFieldEdit(object? sender, RoutedEventArgs e)
    {
        // Opening a picker can move focus while its selected item is being laid out.
        // Committing here synchronously rebinds that same picker and reenters focus loss.
        if (IsLoaded && !handlingSelectionChange)
            QueueCommit(() => ViewModel?.CommitKeyEdit());
    }

    private void HandleKeyProtocolChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || handlingSelectionChange ||
            sender is not ComboBox { SelectedItem: ConfigurationProtocolOption })
        {
            return;
        }
        QueueCommit(() => ViewModel?.CommitKeyProtocolEdit());
    }

    private void HandleKeySystemChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || handlingSelectionChange ||
            sender is not ComboBox { SelectedItem: SystemConfiguration })
        {
            return;
        }
        QueueCommit(() => ViewModel?.CommitKeyEdit());
    }

    private void HandleKeyAlgorithmChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || handlingSelectionChange ||
            sender is not ComboBox { SelectedItem: EncryptionAlgorithmOption })
        {
            return;
        }
        QueueCommit(() => ViewModel?.CommitKeyEdit());
    }

    private void QueueCommit(Action commit)
    {
        int version = ++queuedCommitVersion;
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsLoaded || version != queuedCommitVersion)
                return;
            handlingSelectionChange = true;
            try
            {
                commit();
            }
            finally
            {
                handlingSelectionChange = false;
            }
        }, DispatcherPriority.Background);
    }
}
