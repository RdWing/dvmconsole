using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using DvmConsole.Application;
using DvmConsole.Audio;
using DvmConsole.Ptt;
using System.Globalization;

namespace DvmConsole.Desktop;

// Keeps composition/loading menu mechanics out of the window code-behind.
// The shell supplies its command handler; this helper only renders current
// persisted codeplug/profile choices into a MenuItem.
internal static class MainWindowMenuBuilder
{
    public static void ReplacePttKeyItems(
        MenuItem menu,
        string disabledHeader,
        EventHandler<RoutedEventArgs> clickHandler)
    {
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentException.ThrowIfNullOrWhiteSpace(disabledHeader);
        ArgumentNullException.ThrowIfNull(clickHandler);

        menu.Items.Clear();
        foreach (KeyboardPttKey key in Enum.GetValues<KeyboardPttKey>())
        {
            var item = new MenuItem
            {
                Header = key == KeyboardPttKey.None ? disabledHeader : key.ToString(),
                Tag = key.ToString()
            };
            item.Click += clickHandler;
            menu.Items.Add(item);
        }
    }

    public static void ReplaceItems(
        MenuItem menu,
        IEnumerable<string> entries,
        string emptyHeader,
        EventHandler<RoutedEventArgs> clickHandler)
    {
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(clickHandler);

        string[] values = entries.ToArray();
        menu.Items.Clear();
        if (values.Length == 0)
        {
            menu.Items.Add(new MenuItem { Header = emptyHeader, IsEnabled = false });
            menu.IsEnabled = false;
            return;
        }

        foreach (string value in values)
        {
            var item = new MenuItem { Header = value, Tag = value };
            item.Click += clickHandler;
            menu.Items.Add(item);
        }

        menu.IsEnabled = true;
    }

    public static void ReplaceRecentManagedConfigurationItems(
        MenuItem menu,
        IEnumerable<ConfigurationSummary> entries,
        string emptyHeader,
        EventHandler<RoutedEventArgs> clickHandler)
    {
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(clickHandler);

        ConfigurationSummary[] values = entries
            .Where(summary => !summary.IsLegacyCandidate && summary.LastOpenedAt.HasValue)
            .OrderByDescending(summary => summary.LastOpenedAt)
            .Take(10)
            .ToArray();
        menu.Items.Clear();
        if (values.Length == 0)
        {
            menu.Items.Add(new MenuItem { Header = emptyHeader, IsEnabled = false });
            menu.IsEnabled = false;
            return;
        }

        foreach (ConfigurationSummary summary in values)
        {
            ConfigurationReference reference = new(summary.Id, summary.CurrentRevision);
            string revision = summary.CurrentRevision.Value.ToString("N", CultureInfo.InvariantCulture)[..8];
            string opened = summary.LastOpenedAt!.Value.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
            var header = new StackPanel
            {
                MaxWidth = 520,
                Spacing = 1,
                Children =
                {
                    new TextBlock
                    {
                        Text = summary.Name,
                        FontWeight = FontWeight.SemiBold,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    },
                    new TextBlock
                    {
                        Text = $"Revision {revision} · opened {opened}",
                        Opacity = 0.7,
                        FontSize = 11,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                }
            };
            var item = new MenuItem { Header = header, Tag = reference };
            ToolTip.SetTip(item, $"{summary.Name}\nManaged revision {summary.CurrentRevision.Value:N}");
            item.Click += clickHandler;
            menu.Items.Add(item);
        }

        menu.IsEnabled = true;
    }
}
