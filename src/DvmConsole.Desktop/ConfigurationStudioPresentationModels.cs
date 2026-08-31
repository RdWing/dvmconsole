using DvmConsole.Core.Configuration;
using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

internal sealed class DesktopConfigurationStudioPreviewFactory : IConfigurationStudioPreviewFactory
{
    public IConfigurationChannelPreviewViewModel Create(
        ChannelConfiguration channel,
        double x,
        double y,
        double cardHeight,
        bool darkMode)
    {
        var card = new ChannelViewModel(channel);
        card.SetDarkMode(darkMode);
        return new ConfigurationChannelPreviewViewModel(channel, card, x, y, cardHeight);
    }
}
