using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using System.Diagnostics;

namespace DvmConsole.Desktop;

// Avalonia fork by RdWing.
// Original DVMConsole and fnecore routines by DVMProject authors.
public sealed partial class AboutWindow : Window
{
    private const string RepositoryUrl = "https://github.com/RdWing/dvmconsole";
    private const string UpstreamUrl = "https://github.com/DVMProject/dvmconsole";
    private const string LicenseUrl = "https://github.com/RdWing/dvmconsole/blob/neo/LICENSE";

    public AboutWindow()
    {
        InitializeComponent();
        versionText.Text = MainWindow.ShortApplicationVersion;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        versionText = this.FindControl<TextBlock>("versionText")
            ?? throw new InvalidOperationException("About version control was not created.");
    }

    private void HandleRepositoryClick(object? sender, RoutedEventArgs e)
        => OpenUrl(RepositoryUrl);

    private void HandleUpstreamClick(object? sender, RoutedEventArgs e)
        => OpenUrl(UpstreamUrl);

    private void HandleLicenseClick(object? sender, RoutedEventArgs e)
        => OpenUrl(LicenseUrl);

    private void HandleCloseClick(object? sender, RoutedEventArgs e)
        => Close();

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            DesktopCrashLog.Write("Open About link", exception);
        }
    }
}
