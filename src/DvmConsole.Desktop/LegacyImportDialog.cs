// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace DvmConsole.Desktop;

internal enum LegacyImportDialogDecision
{
    Cancel,
    Decline,
    Import
}

internal sealed record LegacyImportDialogResult(
    LegacyImportDialogDecision Decision,
    LegacyImportSelection Selection);

internal sealed class LegacyImportDialog : Window
{
    private readonly CheckBox? settingsChoice;
    private readonly Dictionary<string, CheckBox> profileChoices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CheckBox> configurationChoices = new(StringComparer.OrdinalIgnoreCase);
    private readonly TaskCompletionSource<LegacyImportDialogResult> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public LegacyImportDialog(LegacyImportDiscovery discovery)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        Title = "Import earlier DVM Console NEO data";
        Width = 620;
        Height = 520;
        MinWidth = 460;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var choices = new StackPanel { Spacing = 8 };
        if (discovery.HasSettings)
        {
            settingsChoice = new CheckBox
            {
                Content = "Operator settings",
                IsChecked = false
            };
            choices.Children.Add(settingsChoice);
        }
        AddChoices(choices, "Settings profiles", discovery.Profiles, profileChoices, value => value);
        AddChoices(
            choices,
            "Managed configurations",
            discovery.Configurations,
            configurationChoices,
            candidate => candidate.Name,
            candidate => candidate.Id);

        var import = new Button { Content = "Import selected", IsEnabled = false };
        foreach (CheckBox choice in AllChoices())
        {
            choice.IsCheckedChanged += (_, _) => import.IsEnabled = AllChoices().Any(item => item.IsChecked == true);
        }
        import.Click += (_, _) => Complete(LegacyImportDialogDecision.Import);
        var later = new Button { Content = "Not now" };
        later.Click += (_, _) => Complete(LegacyImportDialogDecision.Cancel);
        var decline = new Button { Content = "Don't ask again" };
        decline.Click += (_, _) => Complete(LegacyImportDialogDecision.Decline);

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            Margin = new Thickness(20),
            RowSpacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "Import data from the earlier shared DVM Console folder?",
                    FontSize = 18,
                    FontWeight = Avalonia.Media.FontWeight.SemiBold
                },
                AtRow(new TextBlock
                {
                    Text = "Nothing is selected. Choose only the NEO settings, profiles, or managed configurations you recognize. The earlier folder is not changed; recordings, logs, and unknown files are excluded.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                }, 1),
                AtRow(new ScrollViewer { Content = choices }, 2),
                AtRow(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { decline, later, import }
                }, 3)
            }
        };
        Closing += (_, _) => completion.TrySetResult(CreateResult(LegacyImportDialogDecision.Cancel));
    }

    public Task<LegacyImportDialogResult> Result => completion.Task;

    private IEnumerable<CheckBox> AllChoices()
    {
        if (settingsChoice is not null)
            yield return settingsChoice;
        foreach (CheckBox choice in profileChoices.Values)
            yield return choice;
        foreach (CheckBox choice in configurationChoices.Values)
            yield return choice;
    }

    private void Complete(LegacyImportDialogDecision decision)
    {
        completion.TrySetResult(CreateResult(decision));
        Close();
    }

    private LegacyImportDialogResult CreateResult(LegacyImportDialogDecision decision)
        => new(
            decision,
            new LegacyImportSelection(
                settingsChoice?.IsChecked == true,
                profileChoices.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToArray(),
                configurationChoices.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToArray()));

    private static void AddChoices<T>(
        Panel owner,
        string heading,
        IEnumerable<T> values,
        IDictionary<string, CheckBox> target,
        Func<T, string> label,
        Func<T, string>? key = null)
    {
        T[] items = values.ToArray();
        if (items.Length == 0)
            return;
        owner.Children.Add(new TextBlock
        {
            Text = heading,
            Margin = new Thickness(0, 8, 0, 0),
            FontWeight = Avalonia.Media.FontWeight.SemiBold
        });
        foreach (T value in items)
        {
            var choice = new CheckBox { Content = label(value), IsChecked = false };
            target.Add(key?.Invoke(value) ?? label(value), choice);
            owner.Children.Add(choice);
        }
    }

    private static T AtRow<T>(T control, int row) where T : Control
    {
        Grid.SetRow(control, row);
        return control;
    }
}
