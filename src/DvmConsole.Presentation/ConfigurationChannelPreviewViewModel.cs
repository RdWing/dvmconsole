using Avalonia.Media;
using DvmConsole.Core.Configuration;
using System.ComponentModel;

namespace DvmConsole.Presentation;

public sealed class ConfigurationChannelPreviewViewModel :
    INotifyPropertyChanged,
    IConfigurationChannelPreviewViewModel
{
    private static readonly IBrush SelectedBorderBrush = new SolidColorBrush(Color.Parse("#087CF1"));
    private double x;
    private double y;
    private bool isSelected;

    public ConfigurationChannelPreviewViewModel(
        ChannelConfiguration channel,
        IChannelCardViewModel card,
        double x,
        double y,
        double cardHeight)
    {
        Channel = channel ?? throw new ArgumentNullException(nameof(channel));
        Card = card ?? throw new ArgumentNullException(nameof(card));
        this.x = x;
        this.y = y;
        CardHeight = cardHeight;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ChannelConfiguration Channel { get; }
    public IChannelCardViewModel Card { get; }
    public string TalkgroupText => $"TG {Channel.Tgid} - {ConfigurationProtocolCatalog.DisplayName(Channel.Mode)}";
    public double CardWidth => Card.CardWidth;
    public double CardHeight { get; }
    public IBrush BorderBrush => IsSelected ? SelectedBorderBrush : Card.CardBorderBrush;
    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value)
                return;
            isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BorderBrush)));
        }
    }
    public double X
    {
        get => x;
        set
        {
            double next = Math.Max(0, value);
            if (Math.Abs(x - next) < 0.01)
                return;
            x = next;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(X)));
        }
    }
    public double Y
    {
        get => y;
        set
        {
            double next = Math.Max(0, value);
            if (Math.Abs(y - next) < 0.01)
                return;
            y = next;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Y)));
        }
    }
}
