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
            App.SmokeResultPath = ReadOption(args, "--smoke-result=");
            if (App.SmokeWindows)
                App.InitializeSmokeResult();
            App.ConfigurationPath = args.FirstOrDefault(argument => !argument.StartsWith("-", StringComparison.Ordinal));
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            if (App.SmokeWindows)
                App.RecordSmokeFailure(exception);
            DesktopCrashLog.Write("Desktop main loop", exception);
            throw;
        }
    }

    internal static string? ReadOption(IEnumerable<string> args, string prefix)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        string? argument = args.FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal));
        return argument is null ? null : argument[prefix.Length..];
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
