// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using dvmconsole;
using DvmConsole.Avalonia.Dialogs;
using DvmConsole.Avalonia.Persistence;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Core.Configuration;
using DvmConsole.Platform;
using DvmConsole.Platform.Audio;
using DvmConsole.Platform.Audio.Mac;
using DvmConsole.Platform.Hotkeys;
using DvmConsole.Platform.Hotkeys.Mac;
using DvmConsole.Platform.Native;
using System.IO;

namespace DvmConsole.Avalonia
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        /// <summary>
        /// Creates the macOS CoreAudio device catalog, or null when the
        /// host is not macOS or the catalog cannot be constructed. The
        /// catalog constructor throws <see cref="PlatformNotSupportedException"/>
        /// off macOS and may throw <see cref="AudioDeviceException"/> when
        /// CoreAudio listener registration fails; both are expected
        /// runtime conditions and degrade to no audio settings rather
        /// than failing the application.
        /// </summary>
        private static IAudioDeviceCatalog? CreateAudioDeviceCatalog()
        {
            if (!PlatformInfo.IsMacOS)
            {
                return null;
            }

            try
            {
                return new MacAudioDeviceCatalog();
            }
            catch (AudioDeviceException)
            {
                return null;
            }
            catch (PlatformNotSupportedException)
            {
                return null;
            }
        }

        /// <summary>
        /// Creates the global hotkey service for the application
        /// lifetime. On macOS the CGEventTap-backed adapter is composed
        /// with its TCC permission probe: the probe reports whether the
        /// process holds Accessibility and Input Monitoring permission,
        /// so GetCapability can surface PermissionRequired and
        /// registration is denied (never prompted, never bypasses TCC)
        /// until the user grants access in System Settings. On every
        /// other host the unavailable fallback is composed: every
        /// gesture reports unsupported and no hotkey event ever fires.
        /// The application owns the service for its whole lifetime;
        /// disposal is handled by a later concrete factory/lifecycle
        /// slice.
        /// </summary>
        private static IGlobalHotkeyService CreateGlobalHotkeyService()
            => PlatformInfo.IsMacOS
                ? new MacGlobalHotkeyService(new CoreGraphicsEventTap(), new MacPermissionProbe())
                : new UnavailableGlobalHotkeyService();

        /// <summary>
        /// Creates the macOS CGEventSourceKeyState-backed key-state
        /// reader used by the PTT hotkey key-up watchdog, or null on
        /// every other host where the watchdog stays dormant. The
        /// application owns the reader for its whole lifetime.
        /// </summary>
        private static IKeyboardKeyStateReader? CreateKeyStateReader()
            => PlatformInfo.IsMacOS ? new MacKeyStateReader() : null;

        /// <summary>
        /// Runs the startup vocoder-readiness check through the
        /// native-library probe and returns its result. The probe owns
        /// loading, export resolution and handle release; this shell
        /// only maps the outcome. The final readiness line is written to
        /// stdout and flushed so headless SSH/launchd logs observe it: the
        /// stable <c>libvocoder ready</c> line on success, the probe
        /// diagnostic on failure (which also keeps its existing debug
        /// output sink). The check itself never throws.
        /// </summary>
        private static VocoderReadinessResult CheckVocoderReadiness()
        {
            var result = new VocoderReadiness(new NativeLibraryProbe()).Check();

            if (result.IsReady)
            {
                System.Console.WriteLine("libvocoder ready");
            }
            else if (result.Diagnostic is { } diagnostic)
            {
                System.Diagnostics.Debug.WriteLine(diagnostic);
                System.Console.WriteLine(diagnostic);
            }

            System.Console.Out.Flush();

            return result;
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var catalog = CreateAudioDeviceCatalog();
                // The catalog is not owned by the factory (MacAudioStreamFactory
                // catalog-constructor does not dispose it); the MainWindow owns
                // the factory and disposes the audio pipelines on close.
                var streams = catalog is MacAudioDeviceCatalog macCatalog
                    ? new MacAudioStreamFactory(macCatalog)
                    : null;
                var hotkeys = CreateGlobalHotkeyService();
                var persistence = new AudioSettingsPersistence(
                    new SettingsSectionStore(new DefaultFileSystemPaths().SettingsFilePath));
                // Packaged macOS .app: register the bundle resolver for
                // future DllImport-based libvocoder loads in the Platform
                // assembly. The startup readiness probe maps and loads the
                // bundle candidate explicitly, since its assembly-aware
                // TryLoad path does not invoke the DllImportResolver.
                // No-op on every other host and in un-packaged runs.
                MacBundleLibraryResolver.Register(typeof(NativeLibraryProbe).Assembly);
                var vocoderStatus = CheckVocoderReadiness();

                // Load the codeplug from the repo-convention path and
                // seed the FNE slice with its real systems. A missing or
                // unreadable codeplug degrades to no systems (the FNE
                // manager and service stay empty) rather than failing
                // startup; the diagnostic mirrors the vocoder-readiness
                // stdout pattern so headless logs observe it. The load
                // itself never throws.
                var codeplug = CodeplugLoader.LoadFromFile(
                    Path.Combine(System.Environment.CurrentDirectory, "configs", "codeplug.yml"));
                if (!codeplug.Succeeded)
                {
                    System.Console.WriteLine(
                        "codeplug unavailable: " + (codeplug.ErrorMessage ?? "load failed"));
                    System.Console.Out.Flush();
                }

                var mainWindow = new MainWindow(
                    catalog,
                    hotkeys,
                    CreateKeyStateReader(),
                    persistence,
                    vocoderStatus,
                    streams,
                    codeplug.Codeplug?.Systems,
                    // Placeholder codec/traffic seams for the audio
                    // router: the null codec pair keeps the router fully
                    // wired while decoding/encoding is inert, and the
                    // stub sender counts units without sending — the
                    // Platform-native vocoder adapter and the fnecore
                    // traffic adapter land in follow-on slices.
                    new NullVoiceFrameDecoder(),
                    new NullVoiceFrameEncoder(),
                    new StubVoiceTrafficSender());
                mainWindow.FileDialogService =
                    new AvaloniaFileDialogService(mainWindow.StorageProvider);
                desktop.MainWindow = mainWindow;

                if (catalog is MacAudioDeviceCatalog mac)
                {
                    // The catalog raises DevicesChanged from a CoreAudio
                    // callback thread; marshal the refresh to the UI
                    // thread before touching the view-model.
                    mac.DevicesChanged += (_, _) => Dispatcher.UIThread.Post(() =>
                        (mainWindow.DataContext as MainWindowViewModel)?.AudioSettings?.Refresh());
                }
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
