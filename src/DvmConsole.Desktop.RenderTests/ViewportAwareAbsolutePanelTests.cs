// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DvmConsole.Core.Configuration;
using Xunit;

namespace DvmConsole.Desktop.RenderTests;

public sealed class ViewportAwareAbsolutePanelTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void SmallScrollRealizesCardsBeyondPreviousCoverage(bool horizontal)
    {
        ChannelViewModel[] channels = Enumerable.Range(0, 10).Select(CreateChannel).ToArray();
        for (int index = 0; index < channels.Length; index++)
            channels[index].SetWidgetPosition(horizontal ? index * 650 : 0, horizontal ? 0 : index * 650);
        ViewportAwareAbsolutePanel? panel = null;
        var items = new ItemsControl
        {
            Width = horizontal ? 6500 : 400,
            Height = horizontal ? 400 : 6500,
            ItemsSource = channels,
            ItemsPanel = new FuncTemplate<Panel?>(() => panel = new ViewportAwareAbsolutePanel { ItemHeight = 150, Overscan = 240 }),
            ItemTemplate = new FuncDataTemplate<ChannelViewModel>((_, _) => new Border { Width = 300, Height = 150 })
        };
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            Content = items
        };
        var host = new Window { Width = 400, Height = 400, Content = scroll };
        try
        {
            host.Show();
            host.UpdateLayout();
            Assert.NotNull(panel);
            Assert.Null(panel.GetRealizedContainer(1));
            scroll.Offset = horizontal ? new Vector(270, 0) : new Vector(0, 270);
            host.UpdateLayout();
            Assert.NotNull(panel.GetRealizedContainer(1));
            Assert.InRange(panel.RealizedCount, 1, 4);
        }
        finally { host.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(25)]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(1000)]
    public void LargeZonesKeepRealizationBoundedAndRetainInteractionOwners(int itemCount)
    {
        ChannelViewModel[] channels = Enumerable.Range(0, itemCount)
            .Select(CreateChannel)
            .ToArray();
        for (int index = 0; index < channels.Length; index++)
            channels[index].SetWidgetPosition((index % 5) * 250, (index / 5) * 170);

        ViewportAwareAbsolutePanel? panel = null;
        var items = new ItemsControl
        {
            Width = 1250,
            Height = Math.Ceiling(itemCount / 5d) * 170,
            ItemsSource = channels,
            ItemsPanel = new FuncTemplate<Panel?>(() => panel = new ViewportAwareAbsolutePanel
            {
                ItemHeight = 150,
                Overscan = 240
            }),
            ItemTemplate = new FuncDataTemplate<ChannelViewModel>((channel, _) => channel is null
                ? new Border()
                : new Border
                {
                    Width = channel.CardWidth,
                    Height = 150,
                    Focusable = true
                })
        };
        var scroll = new ScrollViewer
        {
            Width = 800,
            Height = 600,
            Content = items
        };
        var host = new Window { Width = 800, Height = 600, Content = scroll };

        try
        {
            host.Show();
            host.UpdateLayout();

            Assert.NotNull(panel);
            Assert.InRange(panel.RealizedCount, 1, Math.Min(itemCount, 50));
            Control retained = Assert.IsAssignableFrom<Control>(panel.GetRealizedContainer(0));
            Control retainedChild = Assert.Single(
                retained.GetVisualDescendants().OfType<Border>());
            ViewportAwareAbsolutePanel.SetIsInteractionPinned(retainedChild, true);

            scroll.Offset = new Vector(0, Math.Max(0, items.Height - scroll.Height));
            host.UpdateLayout();

            Assert.InRange(panel.RealizedCount, 1, Math.Min(itemCount, 51));
            Assert.Same(retained, panel.GetRealizedContainer(0));

            ViewportAwareAbsolutePanel.SetIsInteractionPinned(retainedChild, false);
            channels[0].SetTransmitEnabled(true, 42);
            host.UpdateLayout();
            Assert.Same(retained, panel.GetRealizedContainer(0));
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact]
    public void ScrollIntoViewArrangesAndRevealsAnUnrealizedCard()
    {
        ChannelViewModel[] channels = Enumerable.Range(0, 100)
            .Select(CreateChannel)
            .ToArray();
        for (int index = 0; index < channels.Length; index++)
            channels[index].SetWidgetPosition(0, index * 170);

        ViewportAwareAbsolutePanel? panel = null;
        var items = new ItemsControl
        {
            Width = 400,
            Height = channels.Length * 170,
            ItemsSource = channels,
            ItemsPanel = new FuncTemplate<Panel?>(() => panel = new ViewportAwareAbsolutePanel
            {
                ItemHeight = 150,
                Overscan = 240
            }),
            ItemTemplate = new FuncDataTemplate<ChannelViewModel>((channel, _) => new Border
            {
                Width = channel?.CardWidth ?? 300,
                Height = 150,
                Focusable = true
            })
        };
        var scroll = new ScrollViewer
        {
            Width = 400,
            Height = 600,
            Content = items
        };
        var host = new Window { Width = 400, Height = 600, Content = scroll };

        try
        {
            host.Show();
            host.UpdateLayout();

            Assert.NotNull(panel);
            Assert.Null(panel.GetRealizedContainer(channels.Length - 1));

            items.ScrollIntoView(channels[^1]);
            host.UpdateLayout();

            Assert.NotNull(panel.GetRealizedContainer(channels.Length - 1));
            Assert.True(scroll.Offset.Y > 0);
        }
        finally
        {
            host.Close();
        }
    }

    private static ChannelViewModel CreateChannel(int index)
        => new(new ChannelConfiguration
        {
            Name = $"Channel {index}",
            System = "System",
            Tgid = (100 + index).ToString(),
            Mode = "p25"
        });
}
