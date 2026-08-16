using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;

namespace DvmConsole.Desktop;

public sealed class App : Application
{
    public static string? ConfigurationPath { get; set; }
    public static bool SmokeWindows { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow(ConfigurationPath);
            desktop.MainWindow = mainWindow;
            if (SmokeWindows)
                Dispatcher.UIThread.Post(() => _ = SmokeWindowsAsync(desktop, mainWindow));
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task SmokeWindowsAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow mainWindow)
    {
        try
        {
            mainWindow.Show();
            if (mainWindow.DataContext is not MainWindowViewModel viewModel)
                throw new InvalidOperationException("The main view model was not loaded.");

            bool originalDarkMode = viewModel.DarkMode;
            viewModel.DarkMode = false;
            await Task.Delay(75);
            RequireBackground(mainWindow, "#F3F5F7", "light");
            viewModel.DarkMode = true;
            await Task.Delay(75);
            RequireBackground(mainWindow, "#0D1116", "dark");
            viewModel.DarkMode = originalDarkMode;

            foreach (OperatorToolSection section in Enum.GetValues<OperatorToolSection>())
            {
                var window = new OperatorToolsWindow(viewModel, section);
                window.Show(mainWindow);
                await Task.Delay(75);
                window.Close();
            }

            var history = new CallHistoryWindow(viewModel);
            history.Show(mainWindow);
            await Task.Delay(75);
            history.Close();

            var logs = new DebugLogWindow(viewModel);
            logs.Show(mainWindow);
            await Task.Delay(75);
            logs.Close();

            var documentation = new DocumentationWindow();
            documentation.Show(mainWindow);
            await Task.Delay(75);
            documentation.Close();

            var about = new AboutWindow();
            about.Show(mainWindow);
            await Task.Delay(75);
            about.Close();

            Console.WriteLine("Desktop window smoke passed.");
            desktop.Shutdown(0);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Desktop window smoke failed: {exception}");
            desktop.Shutdown(10);
        }
    }

    private static void RequireBackground(MainWindow window, string expectedColor, string themeName)
    {
        if (window.Background is not ISolidColorBrush brush || brush.Color != Color.Parse(expectedColor))
            throw new InvalidOperationException($"The {themeName} shell background did not update.");
    }
}
