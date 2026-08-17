using Avalonia;
using DvmConsole.Vocoder;

namespace DvmConsole.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        DesktopCrashLog.Install();
        try
        {
            ValidateBuiltInVocoder();
            App.SmokeWindows = args.Contains("--smoke-windows", StringComparer.Ordinal);
            App.ConfigurationPath = args.FirstOrDefault(argument => !argument.StartsWith("-", StringComparison.Ordinal));
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            DesktopCrashLog.Write("Desktop main loop", exception);
            throw;
        }
    }

    private static void ValidateBuiltInVocoder()
    {
        using var backend = new SoftwareVocoderBackend();
        foreach (VocoderMode mode in Enum.GetValues<VocoderMode>())
        {
            using IVocoderSession session = backend.CreateSession(mode);
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        AppBuilder builder = AppBuilder.Configure<App>()
            .UsePlatformDetect();
        if (OperatingSystem.IsMacOS())
        {
            builder = builder.With(new AvaloniaNativePlatformOptions
            {
                RenderingMode =
                [
                    AvaloniaNativeRenderingMode.Metal,
                    AvaloniaNativeRenderingMode.Software
                ]
            });
        }

        return builder.LogToTrace();
    }
}
