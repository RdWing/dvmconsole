using Avalonia;
using DvmConsole.Vocoder;

namespace DvmConsole.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        App.DemoCaptureDirectory = ReadOption(args, "--demo-capture-dir=");
        App.DemoMode = args.Contains("--demo", StringComparer.Ordinal) ||
            !string.IsNullOrWhiteSpace(App.DemoCaptureDirectory);
        DesktopCrashLog.Install(App.DemoMode
            ? Path.Combine(
                Path.GetTempPath(),
                "DvmConsoleNEODemo",
                $"LastCrash-{Environment.ProcessId}.log")
            : null);
        try
        {
            ValidateBuiltInVocoder();
            App.SmokeWindows = args.Contains("--smoke-windows", StringComparer.Ordinal);
            App.SmokeResultPath = ReadOption(args, "--smoke-result=");
            if (App.SmokeWindows)
                App.InitializeSmokeResult();
            App.ConfigurationPath = App.DemoMode
                ? App.ResolveDemoConfigurationPath(AppContext.BaseDirectory)
                : ReadConfigurationPath(args);
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

    internal static string? ReadConfigurationPath(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.FirstOrDefault(argument => !argument.StartsWith('-'));
    }

    private static void ValidateBuiltInVocoder()
    {
        using IVocoderBackend backend = new NativeVocoderFactory().Create();
        foreach (VocoderMode mode in Enum.GetValues<VocoderMode>())
        {
            using IVocoderSession session = backend.CreateSession(mode);
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
#if DVMCONSOLE_MACOS
        AppBuilder builder = AppBuilder.Configure<App>()
            .UseAvaloniaNative()
            .UseSkia();
#elif DVMCONSOLE_WINDOWS
        AppBuilder builder = AppBuilder.Configure<App>()
            .UseWin32()
            .UseSkia();
#else
        AppBuilder builder = AppBuilder.Configure<App>()
            .UsePlatformDetect();
#endif
#if DVMCONSOLE_MACOS
        builder = builder.With(new AvaloniaNativePlatformOptions
        {
            RenderingMode =
            [
                AvaloniaNativeRenderingMode.Metal,
                AvaloniaNativeRenderingMode.Software
            ]
        });
#endif

        return builder.LogToTrace();
    }
}
