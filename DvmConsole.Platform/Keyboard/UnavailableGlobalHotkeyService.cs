// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DvmConsole.Platform.Hotkeys
{
    /// <summary>
    /// Dependency-free fallback <see cref="IGlobalHotkeyService"/> used when no
    /// OS-level global hotkey provider is available. Every gesture is reported
    /// <see cref="HotkeyCapability.Unsupported"/>, registration always fails
    /// with <see cref="HotkeyRegistrationStatus.Unsupported"/> while preserving
    /// the attempted gesture, unregistration is a no-op that always completes,
    /// and <see cref="HotkeyPressed"/> never fires because no OS event source
    /// exists. Dispose is an idempotent no-op and the service stays usable
    /// afterwards. Intended as the temporary contract-compliant implementation
    /// until an OS-specific event-tap or Win32 hotkey implementation is
    /// selected; it performs no native calls.
    /// </summary>
    public sealed class UnavailableGlobalHotkeyService : IGlobalHotkeyService
    {
        /// <summary>
        /// Hotkey press event. It is never raised because this fallback has no
        /// OS event source.
        /// </summary>
#pragma warning disable CS0067 // Intentionally never raised by the unavailable fallback.
        public event EventHandler<HotkeyEventArgs>? HotkeyPressed;
#pragma warning restore CS0067

        /// <summary>
        /// Reports the capability of a gesture on this platform. Always
        /// <see cref="HotkeyCapability.Unsupported"/>: without an OS event
        /// source there is nothing to register against.
        /// </summary>
        public HotkeyCapability GetCapability(HotkeyGesture gesture)
            => HotkeyCapability.Unsupported;

        /// <summary>
        /// Registers a gesture. Always fails with
        /// <see cref="HotkeyRegistrationStatus.Unsupported"/>, echoing the
        /// attempted gesture. Ignores cancellation and never throws.
        /// </summary>
        public Task<HotkeyRegistrationResult> RegisterAsync(
            HotkeyGesture gesture,
            CancellationToken cancellationToken)
            => Task.FromResult(
                new HotkeyRegistrationResult(HotkeyRegistrationStatus.Unsupported, gesture));

        /// <summary>
        /// Unregisters a gesture. A no-op that always completes; ignores
        /// cancellation and never throws.
        /// </summary>
        public Task UnregisterAsync(HotkeyGesture gesture, CancellationToken cancellationToken)
            => Task.CompletedTask;

        /// <summary>
        /// Releases resources. No-op: the service holds no resources, is
        /// idempotent, and remains fully queryable and registrable afterwards.
        /// </summary>
        public void Dispose()
        {
        }
    }
}
