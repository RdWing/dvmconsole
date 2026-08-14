using Avalonia;

namespace DvmConsole.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        App.ConfigurationPath = args.FirstOrDefault(argument => !argument.StartsWith("-", StringComparison.Ordinal));
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
    }
}
