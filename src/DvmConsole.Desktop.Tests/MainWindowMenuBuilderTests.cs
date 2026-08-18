using Avalonia.Controls;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class MainWindowMenuBuilderTests
{
    [Fact]
    public void RecentCodeplugItemPreservesFullPathInTagAndTooltip()
    {
        string path = "/Users/operator/a/very/long/codeplug/location/dispatch.yml";
        var menu = new MenuItem();

        MainWindowMenuBuilder.ReplaceRecentCodeplugItems(menu, [path], "None", (_, _) => { });

        MenuItem item = Assert.IsType<MenuItem>(Assert.Single(menu.Items));
        Assert.Equal(path, item.Tag);
        Assert.Equal(path, ToolTip.GetTip(item));
        StackPanel header = Assert.IsType<StackPanel>(item.Header);
        Assert.Equal("dispatch.yml", Assert.IsType<TextBlock>(header.Children[0]).Text);
        Assert.True(header.MaxWidth <= 560);
    }
}
