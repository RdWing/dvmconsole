using DvmConsole.Core.Settings;

namespace DvmConsole.Desktop;

internal static class DesktopCrashLog
{
    public static string Path => System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(UserSettingsStore.DefaultPath) ?? AppContext.BaseDirectory,
        "LastCrash.log");

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Write("Unhandled process exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Write("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
    }

    public static void Write(string context, Exception? exception)
    {
        try
        {
            string path = Path;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                $"{DateTimeOffset.Now:O} {context}{Environment.NewLine}{exception?.ToString() ?? "No managed exception was supplied."}{Environment.NewLine}");
        }
        catch
        {
            // Crash diagnostics must never become a second application fault.
        }
    }
}
