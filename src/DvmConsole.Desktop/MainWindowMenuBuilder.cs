using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DvmConsole.Desktop;

// Keeps composition/loading menu mechanics out of the window code-behind.
// The shell supplies its command handler; this helper only renders current
// persisted codeplug/profile choices into a MenuItem.
internal static class MainWindowMenuBuilder
{
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
}
