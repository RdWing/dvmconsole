// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DvmConsole.Core.Settings;
using DvmConsole.Desktop;
using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.RenderTests;

public sealed class OperatorUiReviewRenderTests
{
    [AvaloniaTheory]
    [InlineData(980)]
    [InlineData(360)]
    public async Task LargeToneLibraryKeepsCustomAudioReachableAndActionsInsideRows(double width)
    {
        using DemoSessionState state = DemoSessionState.Create();
        var store = new UserSettingsStore(state.UserSettingsPath);
        store.Save(new UserSettings
        {
            TonePresets = Enumerable.Range(1, 60).Select(index => new TonePresetSetting
            {
                Name = $"Station alert {index}",
                Steps = [new() { FrequencyHz = 1000, DurationSeconds = 1 }]
            }).ToList(),
            AlertTones = [new() { Name = "Evacuation announcement", FilePath = "/demo/announcement.wav" }]
        });
        await using var model = MainWindowViewModel.Load(
            Path.Combine(AppContext.BaseDirectory, "Demo", "codeplug.yml"), store, networkDisabledDemo: true);
        var view = new ToneSettingsView { DataContext = model };
        var window = new Window { Width = width, Height = 760, Content = view };
        bool sentAudio = false;
        view.SendAlertToneRequested += (_, _) => sentAudio = true;
        try
        {
            window.Show();
            window.UpdateLayout();
            await App.WaitForRenderAsync();
            var libraryList = view.FindControl<ListBox>("tonePresetList")!;
            ScrollViewer library = libraryList.GetVisualDescendants().OfType<ScrollViewer>().Single();
            Assert.True(library.Viewport.Height <= 240);
            Assert.True(library.Extent.Height > library.Viewport.Height);
            Assert.InRange(libraryList.GetRealizedContainers().Count(), 1, 30);
            Assert.All(view.GetVisualDescendants().OfType<Grid>()
                .Where(grid => grid.Classes.Contains("tone-preset-row")), row =>
            {
                Assert.True(row.Children[1].Bounds.Right <= row.Bounds.Width + 1);
                Assert.True(row.Children[1].Bounds.Bottom <= row.Bounds.Height + 1);
            });
            Capture(window, $"tones-{width:0}");

            view.FindControl<Button>("customAudioSectionLink")!
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            await App.WaitForRenderAsync();
            ScrollViewer scroller = view.FindControl<ScrollViewer>("ToneSettingsScroller")!;
            Control custom = view.FindControl<Control>("CustomAudioSection")!;
            Point position = custom.TranslatePoint(default, scroller)!.Value;
            Assert.InRange(position.Y, -1, scroller.Viewport.Height - 30);
            Assert.False(sentAudio);
            Assert.Equal(60, model.TonePresets.Count);
            Capture(window, $"custom-audio-{width:0}");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ConsoleKeepsCardGeometryAndExposesNamesAndHistoryDays()
    {
        using DemoSessionState state = DemoSessionState.Create();
        var window = new MainWindow(
            Path.Combine(AppContext.BaseDirectory, "Demo", "codeplug.yml"),
            new UserSettingsStore(state.UserSettingsPath), new OperatorViewStore(state.OperatorViewPath), demoMode: true);
        try
        {
            window.Show();
            var model = Assert.IsType<MainWindowViewModel>(window.DataContext);
            model.InitializeDemoScenario();
            model.ToggleActivityReceiveFilter();
            window.UpdateLayout();
            await App.WaitForRenderAsync();
            TabControl systems = window.GetVisualDescendants().OfType<TabControl>()
                .Single(control => control.Name == "systemTabs");
            foreach (TabItem item in systems.GetRealizedContainers().OfType<TabItem>())
                Assert.Equal(((SystemViewModel)item.DataContext!).TabAutomationName, AutomationProperties.GetName(item));
            foreach (ChannelCardContent card in window.GetVisualDescendants().OfType<ChannelCardContent>())
            {
                var channel = Assert.IsType<ChannelViewModel>(card.DataContext);
                Assert.Equal(channel.ReceiveAutomationName, AutomationProperties.GetName(card));
                var border = Assert.IsType<Border>(card.Parent);
                Assert.Equal(channel.CardWidth, border.Width);
                Assert.Equal(model.ChannelCardHeight, border.Height);
            }
            Assert.NotEmpty(model.ActivityCallHistory);
            Assert.True(model.ActivityCallHistory[0].StartsActivityDay);
            Capture(window, "console");
        }
        finally { window.Close(); }
    }

    private static void Capture(Window window, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("DVMCONSOLE_UI_REVIEW_CAPTURES");
        if (string.IsNullOrWhiteSpace(directory))
            return;
        Directory.CreateDirectory(directory);
        App.SaveVisual(window, Path.Combine(directory, name + ".png"));
    }
}
