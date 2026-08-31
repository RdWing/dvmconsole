using Avalonia.Media;
using DvmConsole.Core.Configuration;

namespace DvmConsole.Presentation;

public interface IConfigurationChannelPreviewViewModel
{
    ChannelConfiguration Channel { get; }
    IChannelCardViewModel Card { get; }
    string TalkgroupText { get; }
    double CardWidth { get; }
    double CardHeight { get; }
    IBrush BorderBrush { get; }
    bool IsSelected { get; set; }
    double X { get; set; }
    double Y { get; set; }
}

public interface IConfigurationStudioPreviewFactory
{
    IConfigurationChannelPreviewViewModel Create(
        ChannelConfiguration channel,
        double x,
        double y,
        double cardHeight,
        bool darkMode);
}
