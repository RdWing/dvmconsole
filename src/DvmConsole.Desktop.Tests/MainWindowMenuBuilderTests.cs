using Avalonia.Controls;
using DvmConsole.Application;
using DvmConsole.Audio;
using DvmConsole.Ptt;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class MainWindowMenuBuilderTests
{
    [Fact]
    public void PttKeyItemsAreGeneratedFromTheSharedKeyEnum()
    {
        var menu = new MenuItem();

        MainWindowMenuBuilder.ReplacePttKeyItems(
            menu,
            "Disabled",
            KeyboardPttKey.F8,
            (_, _) => { });

        MenuItem[] items = menu.Items.Cast<MenuItem>().ToArray();
        Assert.Equal(Enum.GetValues<KeyboardPttKey>().Length, items.Length);
        Assert.Equal("Disabled", items[0].Header);
        Assert.Equal("None", items[0].Tag);
        Assert.Equal("F19", items[^1].Header);
        Assert.Equal("F19", items[^1].Tag);
        Assert.All(items, item => Assert.Equal(MenuItemToggleType.Radio, item.ToggleType));
        Assert.Single(items, item => item.IsChecked);
        Assert.True(items.Single(item => Equals(item.Tag, "F8")).IsChecked);
    }

    [Fact]
    public void PttKeySelectionCanBeRefreshedFromTheLiveBinding()
    {
        var menu = new MenuItem();
        MainWindowMenuBuilder.ReplacePttKeyItems(
            menu,
            "Disabled",
            KeyboardPttKey.None,
            (_, _) => { });

        MainWindowMenuBuilder.UpdatePttKeySelection(menu, KeyboardPttKey.Space);

        MenuItem[] items = menu.Items.Cast<MenuItem>().ToArray();
        Assert.Single(items, item => item.IsChecked);
        Assert.True(items.Single(item => Equals(item.Tag, "Space")).IsChecked);
    }

    [Fact]
    public void RecentManagedConfigurationsUseReferencesAndMostRecentFirst()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        ConfigurationSummary older = Summary("Night Dispatch", now.AddHours(-2));
        ConfigurationSummary newer = Summary("Day Dispatch", now.AddMinutes(-5));
        var menu = new MenuItem();

        MainWindowMenuBuilder.ReplaceRecentManagedConfigurationItems(
            menu,
            [older, Summary("Never Opened", null), Summary("Legacy Path", now, isLegacy: true), newer],
            "None",
            (_, _) => { });

        MenuItem[] items = menu.Items.Cast<MenuItem>().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal(
            new ConfigurationReference(newer.Id, newer.CurrentRevision),
            Assert.IsType<ConfigurationReference>(items[0].Tag));
        StackPanel header = Assert.IsType<StackPanel>(items[0].Header);
        Assert.Equal("Day Dispatch", Assert.IsType<TextBlock>(header.Children[0]).Text);
        Assert.Contains("Revision", Assert.IsType<TextBlock>(header.Children[1]).Text, StringComparison.Ordinal);
        Assert.DoesNotContain("/", ToolTip.GetTip(items[0])?.ToString(), StringComparison.Ordinal);
        Assert.True(header.MaxWidth <= 560);
    }

    [Fact]
    public void RecentManagedConfigurationsAreBoundedToTenItems()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        var menu = new MenuItem();

        MainWindowMenuBuilder.ReplaceRecentManagedConfigurationItems(
            menu,
            Enumerable.Range(0, 12).Select(index => Summary($"Configuration {index}", now.AddMinutes(-index))),
            "None",
            (_, _) => { });

        Assert.Equal(10, menu.Items.Count);
    }

    private static ConfigurationSummary Summary(
        string name,
        DateTimeOffset? lastOpenedAt,
        bool isLegacy = false)
        => new(
            ConfigurationId.New(),
            name,
            ConfigurationRevision.New(),
            DateTimeOffset.Now,
            IsActive: false,
            PendingReload: false,
            IsReadOnly: false,
            IsLegacyCandidate: isLegacy,
            LastOpenedAt: lastOpenedAt);
}
