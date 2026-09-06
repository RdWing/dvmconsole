// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Media;
using System.ComponentModel;

namespace DvmConsole.Desktop;

public sealed class ZoneViewModel : INotifyPropertyChanged
{
    private bool darkMode;
    private readonly IBrush activityBrush;
    private Func<bool>? receiveActivityResolver;
    private double widgetCardHeight = 122;
    private double widgetCanvasWidth = 1;
    private double widgetCanvasHeight = 1;

    public ZoneViewModel(
        string name,
        IReadOnlyList<ChannelViewModel> channels,
        IReadOnlyList<WebStreamViewModel> webStreams,
        string? tabColor = null,
        string? tabTextColor = null,
        IBrush? activityBrush = null)
    {
        Name = name;
        Channels = channels;
        WebStreams = webStreams;
        TabColor = tabColor;
        TabTextColor = tabTextColor;
        this.activityBrush = activityBrush ?? SolidBrushCache.Get("#00BE5A");
        foreach (ChannelViewModel channel in Channels)
        {
            channel.PropertyChanged += HandleChannelPropertyChanged;
            channel.WidgetPositionChanged += HandleWidgetPositionChanged;
        }
        foreach (WebStreamViewModel stream in WebStreams)
        {
            stream.PropertyChanged += HandleWebStreamPropertyChanged;
            stream.WidgetPositionChanged += HandleWidgetPositionChanged;
        }
        RecalculateWidgetCanvasBounds();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Name { get; }
    public string TabAutomationName => $"Zone {Name}";
    public IReadOnlyList<ChannelViewModel> Channels { get; }
    public IReadOnlyList<WebStreamViewModel> WebStreams { get; }
    public IReadOnlyList<WebStreamViewModel> WebStreamCards => WebStreams;
    public string? TabColor { get; }
    public string? TabTextColor { get; }
    public IBrush TabBrush => CreateBrush(TabColor, darkMode ? "#151D26" : "#E8EDF3");
    public IBrush TabTextBrush => CreateBrush(TabTextColor, darkMode ? "#DCE3EB" : "#18212B");
    public IBrush ActivityBrush => activityBrush;
    public bool IsReceiving => receiveActivityResolver?.Invoke() ??
        Channels.Any(channel => channel.IsReceivePresentationActive) ||
        WebStreams.Any(stream => stream.IsReceiving);
    public double ActivityBarOpacity => IsReceiving ? 1.0 : 0.12;
    public double WidgetCanvasWidth => widgetCanvasWidth;
    public double WidgetCanvasHeight => widgetCanvasHeight;

    public void SetWidgetCardHeight(double height)
    {
        if (Math.Abs(widgetCardHeight - height) < 0.001)
            return;
        widgetCardHeight = height;
        RecalculateWidgetCanvasBounds();
    }

    public void RefreshWidgetCanvasBounds()
        => RecalculateWidgetCanvasBounds();

    public void SetDarkMode(bool enabled)
    {
        if (darkMode == enabled)
            return;
        darkMode = enabled;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TabBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TabTextBrush)));
        foreach (WebStreamViewModel stream in WebStreams)
            stream.SetDarkMode(enabled);
    }

    internal void SetReceiveActivityResolver(Func<bool> resolver)
    {
        receiveActivityResolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        RefreshReceiveActivity();
    }

    internal void RefreshReceiveActivity()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReceiving)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActivityBarOpacity)));
    }

    private void HandleChannelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChannelViewModel.IsReceivePresentationActive))
            RefreshReceiveActivity();
    }

    private void HandleWebStreamPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WebStreamViewModel.IsReceiving))
            RefreshReceiveActivity();
    }

    private void HandleWidgetPositionChanged(
        object? sender,
        WidgetPositionChangedEventArgs args)
    {
        if (args.IsFinal)
        {
            RecalculateWidgetCanvasBounds();
            return;
        }

        double cardWidth = sender switch
        {
            ChannelViewModel channel => channel.CardWidth,
            WebStreamViewModel stream => stream.CardWidth,
            _ => 0
        };
        if (cardWidth <= 0)
            return;
        SetWidgetCanvasBounds(
            Math.Max(widgetCanvasWidth, args.X + cardWidth + 12),
            Math.Max(widgetCanvasHeight, args.Y + widgetCardHeight + 12));
    }

    private void RecalculateWidgetCanvasBounds()
    {
        double width = 1;
        double height = 1;
        foreach (ChannelViewModel channel in Channels)
        {
            width = Math.Max(width, channel.WidgetX + channel.CardWidth + 12);
            height = Math.Max(height, channel.WidgetY + widgetCardHeight + 12);
        }
        foreach (WebStreamViewModel stream in WebStreams)
        {
            width = Math.Max(width, stream.WidgetX + stream.CardWidth + 12);
            height = Math.Max(height, stream.WidgetY + widgetCardHeight + 12);
        }
        SetWidgetCanvasBounds(width, height);
    }

    private void SetWidgetCanvasBounds(double width, double height)
    {
        if (Math.Abs(widgetCanvasWidth - width) >= 0.01)
        {
            widgetCanvasWidth = width;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WidgetCanvasWidth)));
        }
        if (Math.Abs(widgetCanvasHeight - height) >= 0.01)
        {
            widgetCanvasHeight = height;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WidgetCanvasHeight)));
        }
    }

    private static IBrush CreateBrush(string? color, string fallback)
        => SolidBrushCache.Get(color ?? string.Empty, fallback);
}
