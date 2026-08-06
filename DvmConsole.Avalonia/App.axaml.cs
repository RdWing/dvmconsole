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
using DvmConsole.Platform;
using DvmConsole.Platform.Audio;
using DvmConsole.Platform.Audio.Mac;
using DvmConsole.Platform.Hotkeys;
using DvmConsole.Platform.Native;

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
        /// lifetime. Until an OS-specific event-tap or Win32 hotkey
        /// implementation is selected, the unavailable fallback is
        /// composed on every host — macOS included: every gesture
        /// reports unsupported and no hotkey event ever fires, so the
        /// PTT slice stays unconfigured and the window shows the
        /// capability placeholder. The application owns the service for
        /// its whole lifetime; disposal is handled by a later concrete
        /// factory/lifecycle slice.
        /// </summary>
        private static IGlobalHotkeyService CreateGlobalHotkeyService()
            => new UnavailableGlobalHotkeyService();

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
                var mainWindow = new MainWindow(catalog, hotkeys, null, persistence, vocoderStatus, streams);
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
