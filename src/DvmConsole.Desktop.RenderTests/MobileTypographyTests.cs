// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Media;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DvmConsole.Mobile;
using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.RenderTests;

public sealed class MobileTypographyTests
{
    [AvaloniaTheory]
    [InlineData(1024, 1)]
    [InlineData(1024, 2.5)]
    [InlineData(600, 3.12)]
    public async Task PreferredTextScalesCardsWithoutChangingTheChosenRenderer(double width, double scale)
    {
        await using var console = new MobileConsoleView(ConsoleHostFormFactor.Tablet);
        var window = new Window { Width = width, Height = 768, Content = console };
        window.Styles.Add(new StyleInclude(new Uri("avares://DvmConsole.Mobile/"))
        { Source = new Uri("avares://DvmConsole.Mobile/MobileTypography.axaml") });
        MobileTypography.Apply(window.Resources, scale);
        try
        {
            window.Show();
            window.UpdateLayout();
            var cards = console.GetVisualDescendants().OfType<ChannelCardContent>().ToArray();
            Assert.Equal(ConsoleRendererPreference.Cards, console.EffectiveRenderer);
            Assert.NotEmpty(cards);
            var buttons = cards.SelectMany(card => card.GetVisualDescendants().OfType<Button>()).Where(button => button.IsEffectivelyVisible &&
                (button.Classes.Contains("ptt") || button.Classes.Contains("channel-action"))).ToArray();
            Assert.NotEmpty(buttons);
            foreach (var button in buttons)
            {
                Assert.Equal(13 * scale, button.FontSize);
                Assert.True(button.Bounds.Height >= 44);
                var labels = button.GetVisualDescendants().OfType<TextBlock>().ToArray();
                Assert.NotEmpty(labels);
                Assert.All(labels, label => Assert.Equal(13 * scale, label.FontSize));
                var text = new FormattedText((string)button.Content!, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(button.FontFamily, button.FontStyle, button.FontWeight), button.FontSize, Brushes.Black);
                Assert.True(text.Width <= button.Bounds.Width - button.Padding.Left - button.Padding.Right + 1,
                    $"{button.Content} is clipped at scale {scale}.");
            }
            var list = console.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "List");
            Assert.Equal(14 * scale, list.FontSize);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(320, 2)]
    [InlineData(390, 3)]
    [InlineData(768, 2)]
    public async Task LargePreferredTextKeepsTheConsoleInsideTheViewport(double width, double scale)
    {
        await using var console = new MobileConsoleView(ConsoleHostFormFactor.Phone);
        var window = new Window { Width = width, Height = 844, Content = console, FontSize = 14 * scale };
        MobileTypography.Apply(window.Resources, scale);
        try
        {
            window.Show();
            window.UpdateLayout();
            var scrollers = console.GetVisualDescendants().OfType<ScrollViewer>().Where(view => view.IsEffectivelyVisible).ToArray();
            Assert.NotEmpty(scrollers);
            Assert.All(scrollers, view => Assert.True(view.Extent.Width <= view.Viewport.Width + 1,
                $"Text scale {scale} at width {width}: extent {view.Extent.Width}, viewport {view.Viewport.Width}."));
            foreach (var button in console.GetVisualDescendants().OfType<Button>().Where(button =>
                button.IsEffectivelyVisible && (button.Classes.Contains("list-rx") || button.Classes.Contains("ptt"))))
            {
                var text = new FormattedText((string)button.Content!, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(button.FontFamily, button.FontStyle, button.FontWeight), button.FontSize, Brushes.Black);
                Assert.True(text.Width <= button.Bounds.Width - button.Padding.Left - button.Padding.Right + 1,
                    $"{button.Content} is clipped at scale {scale}: text {text.Width}, button {button.Bounds.Width}.");
            }
            var title = console.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "ConsoleTitle");
            Assert.Equal(19 * scale, title.FontSize);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(2)]
    public void SettingsHeadingKeepsBackBesideTitleAtLargeTextSizes(double scale)
    {
        var back = new Button { Content = "‹ Console", FontSize = 14 * scale, MinHeight = 44 };
        var heading = MobileSettingsPageLayout.Heading("Settings", back);
        var window = new Window { Width = 320, Height = 600, Content = heading };
        MobileTypography.Apply(window.Resources, scale);
        try
        {
            window.Show();
            window.UpdateLayout();
            var title = Assert.Single(heading.Children.OfType<TextBlock>());
            Assert.True(title.Bounds.X >= back.Bounds.Right);
            Assert.True(title.Bounds.Right <= heading.Bounds.Width);
            Assert.True(Math.Abs(title.Bounds.Center.Y - back.Bounds.Center.Y) < 1);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ChangingHostTextSizeUpdatesExistingListLabelsWithoutReplacingSession()
    {
        await using var console = new MobileConsoleView(ConsoleHostFormFactor.Phone);
        var session = console.ApplicationSession;
        var window = new Window { Width = 390, Height = 844, Content = console };
        MobileTypography.Apply(window.Resources, 1);
        try
        {
            window.Show();
            window.UpdateLayout();
            var labels = console.GetVisualDescendants().OfType<TextBlock>()
                .Where(text => text.FontSize == 13).ToArray();
            Assert.NotEmpty(labels);
            MobileTypography.Apply(window.Resources, 1.5);
            window.UpdateLayout();
            // Larger rows can retire off-screen containers; check labels still attached to this host.
            labels = labels.Where(label => TopLevel.GetTopLevel(label) == window).ToArray();
            Assert.NotEmpty(labels);
            Assert.All(labels, label => Assert.Equal(19.5, label.FontSize));
            Assert.Same(session, console.ApplicationSession);
            MobileTypography.Apply(window.Resources, 1);
            Assert.All(labels, label => Assert.Equal(13, label.FontSize));
        }
        finally { window.Close(); }
    }
}
