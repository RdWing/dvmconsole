using Avalonia.Media;
using System.ComponentModel;

namespace DvmConsole.Desktop;

public sealed class ZoneViewModel : INotifyPropertyChanged
{
    private bool darkMode;
    private readonly IBrush activityBrush;
    private Func<bool>? receiveActivityResolver;

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
        this.activityBrush = activityBrush ?? new SolidColorBrush(Color.Parse("#00BE5A"));
        foreach (ChannelViewModel channel in Channels)
            channel.PropertyChanged += HandleChannelPropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Name { get; }
    public IReadOnlyList<ChannelViewModel> Channels { get; }
    public IReadOnlyList<WebStreamViewModel> WebStreams { get; }
    public string? TabColor { get; }
    public string? TabTextColor { get; }
    public IBrush TabBrush => CreateBrush(TabColor, darkMode ? "#151D26" : "#E8EDF3");
    public IBrush TabTextBrush => CreateBrush(TabTextColor, darkMode ? "#DCE3EB" : "#18212B");
    public IBrush ActivityBrush => activityBrush;
    public bool IsReceiving => receiveActivityResolver?.Invoke() ??
        Channels.Any(channel => channel.IsReceivePresentationActive);
    public double ActivityBarOpacity => IsReceiving ? 1.0 : 0.12;
    private double widgetCardHeight = 122;
    public double WidgetCanvasWidth => Math.Max(1, Channels.Count == 0 ? 0 : Channels.Max(channel => channel.WidgetX + channel.CardWidth + 12));
    public double WidgetCanvasHeight => Math.Max(1, Channels.Count == 0 ? 0 : Channels.Max(channel => channel.WidgetY + widgetCardHeight + 12));

    public void SetWidgetCardHeight(double height)
    {
        if (Math.Abs(widgetCardHeight - height) < 0.001)
            return;
        widgetCardHeight = height;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WidgetCanvasHeight)));
    }

    public void RefreshWidgetCanvasBounds()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WidgetCanvasWidth)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WidgetCanvasHeight)));
    }

    public void SetDarkMode(bool enabled)
    {
        if (darkMode == enabled)
            return;
        darkMode = enabled;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TabBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TabTextBrush)));
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
        if (e.PropertyName is nameof(ChannelViewModel.WidgetX) or nameof(ChannelViewModel.WidgetY))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WidgetCanvasWidth)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WidgetCanvasHeight)));
        }
        else if (e.PropertyName == nameof(ChannelViewModel.IsReceivePresentationActive))
            RefreshReceiveActivity();
    }

    private static IBrush CreateBrush(string? color, string fallback)
    {
        try
        {
            return new SolidColorBrush(Color.Parse(
                string.IsNullOrWhiteSpace(color) ? fallback : color.Trim()));
        }
        catch (FormatException)
        {
            return new SolidColorBrush(Color.Parse(fallback));
        }
    }
}
