// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.iOS;
using DvmConsole.Mobile;
using DvmConsole.Presentation;
using Foundation;
using UIKit;

namespace DvmConsole.iOS;

[Register("AppDelegate")]
public sealed class AppDelegate : AvaloniaAppDelegate<App>;

public sealed class App() : MobileApp(
    UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Pad
        ? ConsoleHostFormFactor.Tablet
        : ConsoleHostFormFactor.Phone)
{
    // These subscriptions have the same lifetime as the single application host.
    private readonly List<NSObject> lifecycleObservers = [];
    private IosConsoleHost? consoleHost;
    private IosAccessibilityBridge? accessibility;
    internal IosAccessibilityBridge? AccessibilityBridge => accessibility;

    protected override Func<DvmConsole.Application.IConfigurationLibrary, DvmConsole.Application.ConfigurationReference, CancellationToken, ValueTask<MobileSession>> CreateSession
        => (library, reference, token) =>
        {
            consoleHost ??= new IosConsoleHost();
            consoleHost.SetForeground(UIApplication.SharedApplication.ApplicationState == UIApplicationState.Active);
            return consoleHost.CreateAsync(library, reference, token);
        };

    public override void OnFrameworkInitializationCompleted()
    {
        if (lifecycleObservers.Count == 0)
        {
            lifecycleObservers.Add(UIApplication.Notifications.ObserveWillResignActive((_, _) => UpdateForeground(false)));
            lifecycleObservers.Add(UIApplication.Notifications.ObserveDidEnterBackground((_, _) => UpdateForeground(false)));
            lifecycleObservers.Add(UIApplication.Notifications.ObserveDidBecomeActive((_, _) => UpdateForeground(true)));
            UpdateForeground(UIApplication.SharedApplication.ApplicationState == UIApplicationState.Active);
        }
        // Avalonia/Skia matches families, not UIKit PostScript face names.
        // .SFUI-Regular and its weighted variants silently fall back to Helvetica.
        using (var systemFont = UIFont.SystemFontOfSize(17))
        {
            Avalonia.Media.FontManager.Current.AddFontCollection(
                new IosSystemFontCollection(systemFont?.FamilyName ?? ".AppleSystemUIFont"));
            var family = IosSystemFontCollection.Family;
            Resources["MobileSystemFontFamily"] = family;
            Resources["MobileBoldFontFamily"] = family;
            Resources["MobileSemiboldFontFamily"] = family;
        }
        UpdatePreferredTextSize();
        lifecycleObservers.Add(UIApplication.Notifications.ObserveContentSizeCategoryChanged((_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(UpdatePreferredTextSize)));
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime { MainView: { } main })
        {
            main.Loaded += (_, _) => AttachAccessibility(main);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => AttachAccessibility(main));
        }
        _ = Task.Run(IosDiagnostics.RunRequestedAsync);
    }

    private void AttachAccessibility(Avalonia.Controls.Control root)
    {
        if (accessibility is not null) return;
        // The pinned Avalonia host uses the application delegate's window and
        // does not require a scene manifest.
        if (UIApplication.SharedApplication.Delegate is AppDelegate { Window: { } primary } &&
            FindAvaloniaView(primary) is { } primaryView)
        {
            accessibility = new IosAccessibilityBridge(root, primaryView);
            return;
        }
        foreach (var scene in UIApplication.SharedApplication.ConnectedScenes.OfType<UIWindowScene>())
        foreach (var window in scene.Windows)
        {
            if (FindAvaloniaView(window) is not { } view) continue;
            accessibility = new IosAccessibilityBridge(root, view);
            return;
        }
    }

    private static AvaloniaView? FindAvaloniaView(UIView view)
    {
        if (view is AvaloniaView avalonia) return avalonia;
        foreach (var child in view.Subviews)
            if (FindAvaloniaView(child) is { } found) return found;
        return null;
    }

    private void UpdatePreferredTextSize()
    {
        using var body = UIFont.PreferredBody;
        SetPreferredTextScale(body is null ? 1 : (double)body.PointSize / 17d);
    }

    private void UpdateForeground(bool foreground)
    {
        SetHostForeground(foreground);
        consoleHost?.SetForeground(foreground);
    }

    // Qualification sessions own their own endpoints and isolated storage.
    protected override bool RestoreSavedSessionOnStartup => !IosDiagnostics.IsRequested;

    protected override IMobileLayoutPreferences LayoutPreferences { get; } = new IosLayoutPreferences();

    protected override Func<Avalonia.Controls.Control> CreateAudioRoutePicker
        => () => new IosAudioRoutePicker();

    protected override Func<Avalonia.Controls.Control> CreateAudioInputSettings
        => () => new IosAudioInputSettings(() => consoleHost?.CreateInputSelectionContext()
            ?? throw new InvalidOperationException("Open a configuration before choosing its microphone."));

    protected override Func<DvmConsole.Application.IConsoleHelpCatalog> CreateHelpCatalog
        => () => DvmConsole.Storage.DocumentationCatalog.Open(Path.Combine(NSBundle.MainBundle.BundlePath, "Documentation"));

    protected override Func<DvmConsole.Application.IConfigurationLibrary, DvmConsole.Application.ConfigurationReference, CancellationToken, ValueTask<MobileStudioSession>> CreateStudio
        => IosConfigurationStudio.OpenAsync;

    protected override Func<DvmConsole.Application.IConfigurationLibrary, DvmConsole.Application.ConfigurationDraft, CancellationToken, ValueTask<MobileStudioSession>> CreateDraftStudio
        => IosConfigurationStudio.OpenDraftAsync;

    protected override Func<DvmConsole.Application.IConfigurationExportArchive> CreateExportArchive
        => IosConfigurationStorage.CreateExportArchive;

    protected override Func<DvmConsole.Application.IConfigurationLibrary>? CreateConfigurationLibrary
        => IosConfigurationStorage.Open;
}
