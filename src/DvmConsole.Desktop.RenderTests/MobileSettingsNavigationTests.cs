// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DvmConsole.Application;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using DvmConsole.Mobile;
using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.RenderTests;

public sealed class MobileSettingsNavigationTests
{
    [AvaloniaFact]
    public async Task PreferenceSwitchSupportsPointerInputAcrossTheWholeRow()
    {
        await using var console = new MobileConsoleView(ConsoleHostFormFactor.Phone);
        var toggle = new ToggleSwitch { Content = "Preference", KnobTransitions = new Avalonia.Animation.Transitions() };
        var settings = new MobileSettingsView(toggle, ConsoleHostFormFactor.Phone, console);
        var window = new Window { Content = settings, Width = 390, Height = 700 };
        window.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://DvmConsole.Mobile/"))
        { Source = new Uri("avares://DvmConsole.Mobile/MobileSettingsStyles.axaml") });
        try
        {
            settings.OpenConfigurationLibrary();
            window.Show(); window.UpdateLayout();
            var point = toggle.TranslatePoint(new Point(20, toggle.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
            Assert.True(toggle.IsChecked);
            var knob = toggle.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "PART_MovingKnobs");
            Assert.Equal(20, Canvas.GetLeft(knob));
            window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
            Assert.False(toggle.IsChecked);
            Assert.Equal(0, Canvas.GetLeft(knob));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TabletResizePreservesTheActiveEditorWithoutDetachingIt()
    {
        await using var console = new MobileConsoleView(ConsoleHostFormFactor.Tablet);
        var draft = new TextBox { Text = "Unsaved draft" };
        var settings = new MobileSettingsView(draft, ConsoleHostFormFactor.Tablet, console);
        var window = new Window { Content = settings, Width = 1024, Height = 768 };
        int detached = 0;
        draft.DetachedFromVisualTree += (_, _) => detached++;
        try
        {
            window.Show();
            settings.OpenConfigurationLibrary();
            window.UpdateLayout();
            foreach (double width in new[] { 600d, 1024d, 700d, 1200d })
            {
                window.Width = width;
                window.UpdateLayout();
                Assert.Contains(draft, settings.GetVisualDescendants());
                Assert.True(draft.IsEffectivelyVisible);
                Assert.Equal("Unsaved draft", draft.Text);
                Assert.Equal(0, detached);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SingleLineAndNumericTextAreVerticallyCenteredInTouchFields()
    {
        await using var console = new MobileConsoleView(ConsoleHostFormFactor.Phone);
        var text = new TextBox { Text = "Channel name", Height = 48 };
        var number = new NumericUpDown { Value = 125, Height = 48 };
        var fields = new StackPanel();
        fields.Children.Add(text); fields.Children.Add(number);
        var settings = new MobileSettingsView(fields, ConsoleHostFormFactor.Phone, console);
        var window = new Window { Content = settings, Width = 390, Height = 700 };
        window.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://DvmConsole.Mobile/"))
        { Source = new Uri("avares://DvmConsole.Mobile/MobileSettingsStyles.axaml") });
        try
        {
            settings.OpenConfigurationLibrary();
            window.Show(); window.UpdateLayout();
            foreach (var field in new Control[] { text, number })
            {
                var presenter = Assert.Single(field.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.TextPresenter>());
                var origin = presenter.TranslatePoint(default, field)!.Value;
                Assert.InRange(Math.Abs(origin.Y + presenter.Bounds.Height / 2 - field.Bounds.Height / 2), 0, 1);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task NativeRoutePickerIsOutsideScrollingSettingsAndDetachesOnReturn()
    {
        await using var console = new MobileConsoleView(ConsoleHostFormFactor.Phone);
        var picker = new Button { Content = "Native route", Width = 48, Height = 48 };
        var settings = new MobileSettingsView(new TextBox(), ConsoleHostFormFactor.Phone, () => console,
            audioRoutePicker: picker);
        var window = new Window { Content = settings, Width = 390, Height = 700 };
        try
        {
            window.Show(); window.UpdateLayout();
            var overview = settings.Content;
            Assert.DoesNotContain(picker, settings.GetVisualDescendants());
            settings.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Audio  ›"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Assert.Contains(picker, settings.GetVisualDescendants());
            Assert.DoesNotContain(picker.GetVisualAncestors(), ancestor => ancestor is ScrollViewer);
            settings.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "‹ Settings"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Assert.Same(overview, settings.Content);
            Assert.DoesNotContain(picker, settings.GetVisualDescendants());
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(SessionReplacementFollowUpPhase.RetiredSessionCleanup, "cleanup of the previous session")]
    [InlineData(SessionReplacementFollowUpPhase.SelectedWebStreamRestore, "Use Resume listening")]
    public async Task CommittedReplacementFailureExplainsTheCurrentSessionState(
        SessionReplacementFollowUpPhase phase, string expected)
    {
        await using var console = new MobileConsoleView(ConsoleHostFormFactor.Phone);
        var library = new TextBlock { Text = "Library" };
        var settings = new MobileSettingsView(library, ConsoleHostFormFactor.Phone, console);
        var window = new Window { Width = 390, Height = 600, Content = settings };
        try
        {
            window.Show();
            settings.OpenConfigurationLibrary();
            settings.ShowSessionFollowUpFailure(phase, "Device unavailable");
            window.UpdateLayout();
            var message = settings.GetVisualDescendants().OfType<TextBlock>()
                .Single(text => text.Text?.Contains("Device unavailable") == true);
            Assert.True(message.IsVisible);
            Assert.Contains(expected, message.Text);
            Assert.DoesNotContain(library, settings.GetVisualDescendants());
            settings.ClearStartupFailure();
            Assert.False(message.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(ConsoleHostFormFactor.Phone, 390)]
    [InlineData(ConsoleHostFormFactor.Tablet, 1024)]
    public async Task EmptyConsoleOffersConfigurationWithoutExampleChannels(ConsoleHostFormFactor formFactor, double width)
    {
        await using var session = new ConsoleApplicationSession(new ConsoleTopologySnapshot(null, [], [], []),
            ConsoleRuntimeSnapshot.Empty, new NoOpConsoleCommands());
        await using var console = new MobileConsoleView(formFactor, applicationSession: session, ownsSession: false);
        var library = new TextBlock { Text = "Configuration Library" };
        var settings = new MobileSettingsView(library, formFactor, console);
        var window = new Window { Width = width, Height = 600, Content = console };
        console.ConfigurationRequested += (_, _) =>
        {
            settings.OpenConfigurationLibrary();
            window.Content = settings;
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            Assert.Empty(console.ApplicationSession.Topology.Channels);
            var open = console.GetVisualDescendants().OfType<Button>().Single(button =>
                Equals(button.Content, "Create or import configuration"));
            Assert.True(open.Bounds.Height >= 44);
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Assert.Contains(library, settings.GetVisualDescendants());
            Assert.Same(settings, window.Content);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(390, 320)]
    [InlineData(1024, 320)]
    public async Task ConsoleReturnStaysVisibleWhenSettingsScrolls(double width, double height)
    {
        await using var console = new MobileConsoleView(ConsoleHostFormFactor.Phone);
        var settings = new MobileSettingsView(new TextBox(), ConsoleHostFormFactor.Phone, () => console,
            audioRoutePicker: new Button { Content = "System route", Height = 48 });
        var window = new Window { Width = width, Height = height, Content = settings };
        try
        {
            window.Show();
            window.UpdateLayout();
            var back = settings.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "‹ Console"));
            var scroll = settings.GetVisualDescendants().OfType<ScrollViewer>().Single();
            var before = back.TranslatePoint(default, settings);
            scroll.Offset = new Vector(0, scroll.Extent.Height);
            window.UpdateLayout();
            Assert.True(scroll.Offset.Y > 0);
            Assert.Equal(before, back.TranslatePoint(default, settings));
            Assert.True(back.Bounds.Height >= 44);
            Assert.DoesNotContain(scroll, back.GetVisualAncestors());
            Assert.True(back.TranslatePoint(default, settings)!.Value.Y >= 0);
        }
        finally { window.Close(); }
    }
}
