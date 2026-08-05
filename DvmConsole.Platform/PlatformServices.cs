// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Threading.Tasks;
using DvmConsole.Platform.Audio;
using DvmConsole.Platform.Dialogs;
using DvmConsole.Platform.Hotkeys;
using DvmConsole.Platform.Native;

namespace DvmConsole.Platform
{
    /// <summary>
    /// Composition root for the dependency-free DvmConsole.Platform service
    /// surface: audio streams, device catalog, file dialogs, global hotkeys and
    /// the native library probe. Disposal propagates to every injected service
    /// and is idempotent.
    /// </summary>
    public sealed class PlatformServices : IAsyncDisposable
    {
        private bool _disposed;

        /// <summary>
        /// Injects the platform services.
        /// </summary>
        /// <param name="audioStreams">Audio stream factory.</param>
        /// <param name="devices">Audio device catalog.</param>
        /// <param name="dialogs">File and folder dialog service.</param>
        /// <param name="hotkeys">Global hotkey service.</param>
        /// <param name="nativeProbe">Native library probe.</param>
        /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
        public PlatformServices(
            IAudioStreamFactory audioStreams,
            IAudioDeviceCatalog devices,
            IFileDialogService dialogs,
            IGlobalHotkeyService hotkeys,
            INativeLibraryProbe nativeProbe)
        {
            AudioStreams = audioStreams ?? throw new ArgumentNullException(nameof(audioStreams));
            Devices = devices ?? throw new ArgumentNullException(nameof(devices));
            Dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
            Hotkeys = hotkeys ?? throw new ArgumentNullException(nameof(hotkeys));
            NativeProbe = nativeProbe ?? throw new ArgumentNullException(nameof(nativeProbe));
        }

        /// <summary>Audio stream factory (inputs, outputs, file player).</summary>
        public IAudioStreamFactory AudioStreams { get; }

        /// <summary>Audio device catalog.</summary>
        public IAudioDeviceCatalog Devices { get; }

        /// <summary>File and folder dialog service.</summary>
        public IFileDialogService Dialogs { get; }

        /// <summary>Global hotkey service.</summary>
        public IGlobalHotkeyService Hotkeys { get; }

        /// <summary>Native library probe.</summary>
        public INativeLibraryProbe NativeProbe { get; }

        /// <summary>Stable platform surface name.</summary>
        public string Name => "DvmConsole.Platform";

        /// <summary>Version of the platform surface assembly.</summary>
        public string Version =>
            typeof(PlatformServices).Assembly.GetName().Version?.ToString() ?? "0.0.0";

        /// <summary>
        /// Disposes every injected service: asynchronously for IAsyncDisposable
        /// services, synchronously for IDisposable services. Idempotent.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            await AudioStreams.DisposeAsync().ConfigureAwait(false);
            await Devices.DisposeAsync().ConfigureAwait(false);
            await Dialogs.DisposeAsync().ConfigureAwait(false);

            Hotkeys.Dispose();

            await NativeProbe.DisposeAsync().ConfigureAwait(false);
        }
    }
}
