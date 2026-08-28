using Avalonia.Controls;
using DvmConsole.Audio;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class MainWindowMenuBuilderTests
{
    [Fact]
    public void PttKeyItemsAreGeneratedFromTheSharedKeyEnum()
    {
        var menu = new MenuItem();

        MainWindowMenuBuilder.ReplacePttKeyItems(menu, "Disabled", (_, _) => { });

        MenuItem[] items = menu.Items.Cast<MenuItem>().ToArray();
        Assert.Equal(Enum.GetValues<KeyboardPttKey>().Length, items.Length);
        Assert.Equal("Disabled", items[0].Header);
        Assert.Equal("None", items[0].Tag);
        Assert.Equal("F19", items[^1].Header);
        Assert.Equal("F19", items[^1].Tag);
    }

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
