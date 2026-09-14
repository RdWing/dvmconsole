// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Interactivity;

namespace DvmConsole.Presentation;

public sealed partial class ToneSettingsView : UserControl
{
    public ToneSettingsView()
    {
        InitializeComponent();
        ResponsiveSettingsDensity.Attach(this);
    }

    public event EventHandler<DtmfPresetEventArgs>? UseDtmfPresetRequested;
    public event EventHandler<DtmfPresetEventArgs>? SendDtmfPresetRequested;
    public event EventHandler<DtmfPresetEventArgs>? DeleteDtmfPresetRequested;
    public event EventHandler<TonePresetEventArgs>? UseTonePresetRequested;
    public event EventHandler<TonePresetEventArgs>? SendTonePresetRequested;
    public event EventHandler<TonePresetEventArgs>? DeleteTonePresetRequested;
    public event EventHandler? SendQuickCallRequested;
    public event EventHandler? AddToneStepRequested;
    public event EventHandler? AddSilenceStepRequested;
    public event EventHandler<ToneSequenceStepEventArgs>? RemoveToneStepRequested;
    public event EventHandler<ToneSequenceStepEventArgs>? MoveToneStepUpRequested;
    public event EventHandler<ToneSequenceStepEventArgs>? MoveToneStepDownRequested;
    public event EventHandler? ImportAlertToneRequested;
    public event EventHandler<AlertToneEventArgs>? SendAlertToneRequested;
    public event EventHandler<AlertToneEventArgs>? DeleteAlertToneRequested;

    private Expander? patternEditor;
    private bool focusedNavigation;
    private string selectedSection = "TonePatternSection";
    private static readonly string[] SectionNames =
        ["DtmfSection", "TonePatternSection", "PagingSection", "CustomAudioSection"];

    /// <summary>Mobile uses one scrolling surface and retains editor drafts across tone families.</summary>
    public void UseFocusedNavigation(Control options)
    {
        if (focusedNavigation) return;
        focusedNavigation = true;
        var navigation = this.FindControl<WrapPanel>("ToneFamilyNavigation")!;
        var heading = this.FindControl<TextBlock>("ToneSettingsHeading")!;
        heading.Text = "Tones";
        var familyButtons = navigation.Children.OfType<Button>().ToDictionary(button => (string)button.Tag!);
        navigation.Children.Clear();
        var segments = new Avalonia.Controls.Primitives.UniformGrid { Columns = 4 };
        foreach (var (section, label) in new[] { ("TonePatternSection", "Patterns"), ("DtmfSection", "DTMF"),
            ("PagingSection", "QCII"), ("CustomAudioSection", "Audio") })
        {
            var button = familyButtons[section];
            button.Content = label;
            button.Classes.Add("tone-family-segment");
            button.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            button.Padding = new Thickness(6, 8);
            segments.Children.Add(button);
        }
        segments.SizeChanged += (_, _) =>
        {
            double minimum = 72 * Math.Max(1, familyButtons.Values.Max(button => button.FontSize) / 14);
            int columns = segments.Bounds.Width >= 4 * minimum ? 4 : 2;
            if (segments.Columns != columns) segments.Columns = columns;
        };
        var segmentHost = new Border { Child = segments, CornerRadius = new CornerRadius(10), Padding = new Thickness(3) };
        segmentHost.Bind(Border.BackgroundProperty, segmentHost.GetResourceObservable("ControlBorderBrush"));
        var navigationHost = (StackPanel)navigation.Parent!;
        int navigationIndex = navigationHost.Children.IndexOf(navigation);
        navigationHost.Children.Remove(navigation);
        navigationHost.Children.Insert(navigationIndex, segmentHost);
        var pattern = this.FindControl<StackPanel>("PatternEditorBody")!;
        var patternSection = (StackPanel)pattern.Parent!;
        patternSection.Children.Remove(pattern);
        patternEditor = new Expander
        {
            Header = "Create or edit pattern",
            Content = pattern,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };
        patternSection.Children.Add(patternEditor);
        this.FindControl<Control>("PatternExchangeActions")!.IsVisible = false;
        var scroller = this.FindControl<ScrollViewer>("ToneSettingsScroller")!;
        var sections = (StackPanel)scroller.Content!;
        sections.Children.Insert(0, options);
        SelectSection(selectedSection);
        AttachedToVisualTree += (_, _) => SelectSection(selectedSection);
    }

    private void SelectSection(string name)
    {
        selectedSection = name;
        foreach (string section in SectionNames)
            this.FindControl<Control>(section)!.IsVisible = section == name;
        foreach (var button in this.GetLogicalDescendants().OfType<Button>())
            if (button.Tag is string target && SectionNames.Contains(target))
                button.Classes.Set("selected-tone-family", target == name);
        this.FindControl<ScrollViewer>("ToneSettingsScroller")!.Offset = default;
    }

    private void HandleSectionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string sectionName })
        {
            if (focusedNavigation) SelectSection(sectionName);
            else this.FindControl<Control>(sectionName)?.BringIntoView();
        }
    }

    private void HandlePresetRowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid row || row.Children.Count != 2)
            return;

        // Preserve room for the preset name beside the row actions.
        // Change layout only at the breakpoint to avoid repeated layout work.
        bool stackActions = e.NewSize.Width < 560;
        if (stackActions == (row.ColumnDefinitions.Count == 1))
            return;
        row.ColumnDefinitions = new ColumnDefinitions(stackActions ? "*" : "*,Auto");
        row.RowDefinitions = new RowDefinitions(stackActions ? "Auto,Auto" : "Auto");
        Grid.SetColumn(row.Children[1], stackActions ? 0 : 1);
        Grid.SetRow(row.Children[1], stackActions ? 1 : 0);
    }

    private void HandleUseDtmfPresetClick(object? sender, RoutedEventArgs e)
        => PublishDtmfPreset(sender, UseDtmfPresetRequested);
    private void HandleSendDtmfPresetClick(object? sender, RoutedEventArgs e)
        => PublishDtmfPreset(sender, SendDtmfPresetRequested);
    private void HandleDeleteDtmfPresetClick(object? sender, RoutedEventArgs e)
        => PublishDtmfPreset(sender, DeleteDtmfPresetRequested);
    private void HandleUseTonePresetClick(object? sender, RoutedEventArgs e)
    {
        PublishTonePreset(sender, UseTonePresetRequested);
        if (patternEditor is not null) patternEditor.IsExpanded = true;
    }

    public Control CreatePatternExchangeButtons()
    {
        var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        var import = new Button { Content = "↓ Import", MinHeight = 44 };
        var export = new Button { Content = "↑ Export", MinHeight = 44 };
        Avalonia.Automation.AutomationProperties.SetName(import, "Import tone patterns");
        Avalonia.Automation.AutomationProperties.SetName(export, "Export tone patterns");
        import.Click += HandleImportPatternsClick;
        export.Click += HandleExportPatternsClick;
        actions.Children.Add(import);
        actions.Children.Add(export);
        return actions;
    }

    public Button CreatePatternExchangeMenu()
    {
        var import = new MenuItem { Header = "Import patterns" };
        var export = new MenuItem { Header = "Export patterns" };
        import.Click += HandleImportPatternsClick;
        export.Click += HandleExportPatternsClick;
        var button = new Button
        {
            Content = "•••",
            MinHeight = 44,
            MinWidth = 44,
            Flyout = new MenuFlyout { ItemsSource = new[] { import, export } }
        };
        Avalonia.Automation.AutomationProperties.SetName(button, "Tone pattern import and export");
        return button;
    }
    private void HandleSendTonePresetClick(object? sender, RoutedEventArgs e)
        => PublishTonePreset(sender, SendTonePresetRequested);
    private void HandleDeleteTonePresetClick(object? sender, RoutedEventArgs e)
        => PublishTonePreset(sender, DeleteTonePresetRequested);
    private void HandleSendQuickCallClick(object? sender, RoutedEventArgs e)
        => SendQuickCallRequested?.Invoke(this, EventArgs.Empty);
    private void HandleAddToneStepClick(object? sender, RoutedEventArgs e)
        => AddToneStepRequested?.Invoke(this, EventArgs.Empty);
    private void HandleAddSilenceStepClick(object? sender, RoutedEventArgs e)
        => AddSilenceStepRequested?.Invoke(this, EventArgs.Empty);
    private void HandleRemoveToneStepClick(object? sender, RoutedEventArgs e)
        => PublishToneStep(sender, RemoveToneStepRequested);
    private void HandleMoveToneStepUpClick(object? sender, RoutedEventArgs e)
        => PublishToneStep(sender, MoveToneStepUpRequested);
    private void HandleMoveToneStepDownClick(object? sender, RoutedEventArgs e)
        => PublishToneStep(sender, MoveToneStepDownRequested);
    private void HandleImportAlertToneClick(object? sender, RoutedEventArgs e)
        => ImportAlertToneRequested?.Invoke(this, EventArgs.Empty);
    private void HandleSendAlertToneClick(object? sender, RoutedEventArgs e)
        => PublishAlertTone(sender, SendAlertToneRequested);
    private void HandleDeleteAlertToneClick(object? sender, RoutedEventArgs e)
        => PublishAlertTone(sender, DeleteAlertToneRequested);

    private void PublishDtmfPreset(object? sender, EventHandler<DtmfPresetEventArgs>? handler)
    {
        if (sender is Button { Tag: IDtmfPresetViewModel preset })
            handler?.Invoke(this, new DtmfPresetEventArgs(preset));
    }

    private void PublishTonePreset(object? sender, EventHandler<TonePresetEventArgs>? handler)
    {
        if (sender is Button { Tag: ITonePresetViewModel preset })
            handler?.Invoke(this, new TonePresetEventArgs(preset));
    }

    private void PublishToneStep(object? sender, EventHandler<ToneSequenceStepEventArgs>? handler)
    {
        if (sender is Button { Tag: IToneSequenceStepViewModel step })
            handler?.Invoke(this, new ToneSequenceStepEventArgs(step));
    }

    private void PublishAlertTone(object? sender, EventHandler<AlertToneEventArgs>? handler)
    {
        if (sender is Button { Tag: IAlertToneViewModel tone })
            handler?.Invoke(this, new AlertToneEventArgs(tone));
    }
}
