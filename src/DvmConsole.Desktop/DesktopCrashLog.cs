// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Settings;

namespace DvmConsole.Desktop;

internal static class DesktopCrashLog
{
    private static string? pathOverride;

    public static string Path => Volatile.Read(ref pathOverride) ?? System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(UserSettingsStore.DefaultPath) ?? AppContext.BaseDirectory,
        "LastCrash.log");

    public static void Install(string? isolatedPath = null)
    {
        if (!string.IsNullOrWhiteSpace(isolatedPath))
            Volatile.Write(ref pathOverride, System.IO.Path.GetFullPath(isolatedPath));
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
            AppDataFileProtection.EnsureDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                $"{DateTimeOffset.Now:O} {context}{Environment.NewLine}{exception?.ToString() ?? "No managed exception was supplied."}{Environment.NewLine}");
            AppDataFileProtection.EnsureFile(path);
        }
        catch
        {
            // Crash diagnostics must never become a second application fault.
        }
    }
}
