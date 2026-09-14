// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Configuration.Yaml;
using Foundation;
using UIKit;

namespace DvmConsole.iOS;

internal static class IosConfigurationStorage
{
    public static IConfigurationLibrary Open()
        => new ManagedConfigurationLibrary(Path.Combine(OpenRoot(), "Configurations"));

    public static IConfigurationExportArchive CreateExportArchive()
        => new DvmConsole.Storage.ConfigurationExportArchive(Path.Combine(OpenRoot(), "Exports"));

    internal static string OpenRoot()
    {
        if (!UIApplication.SharedApplication.ProtectedDataAvailable)
            throw new InvalidOperationException("Configuration storage is locked. Unlock this device and refresh the library.");
        using NSUrl support = NSFileManager.DefaultManager.GetUrls(
            NSSearchPathDirectory.ApplicationSupportDirectory, NSSearchPathDomain.User)[0];
        string root = Path.Combine(support.Path ?? throw new IOException("Application Support is unavailable."), "ConsoleNEO");
        Directory.CreateDirectory(root);
        if (!NSFileManager.DefaultManager.SetAttributes(new NSFileAttributes
            { ProtectionKey = NSFileProtection.CompleteUntilFirstUserAuthentication }, root, out NSError? error))
        {
            string reason = error?.LocalizedDescription ?? "Unknown storage protection error.";
            error?.Dispose();
            throw new IOException(reason);
        }
        return root;
    }
}
