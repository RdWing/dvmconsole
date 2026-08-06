// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DvmConsole.Platform.Hotkeys.Mac
{
    /// <summary>
    /// CGEventTap-backed <see cref="IGlobalHotkeyService"/> with a TCC
    /// permission model. <see cref="GetCapability"/> reports
    /// <see cref="HotkeyCapability.PermissionRequired"/> when the injected
    /// probe denies (the probe result is cached after the first query),
    /// <see cref="HotkeyCapability.Available"/> when permission is granted
    /// and the gesture is mappable, and <see cref="HotkeyCapability.Unsupported"/>
    /// for the None key, unmappable keys and off-macOS. <see cref="RegisterAsync"/>
    /// returns <see cref="HotkeyRegistrationStatus.PermissionDenied"/>
    /// (preserving the gesture) when permission is missing — it never prompts
    /// and never bypasses TCC, and the event tap is never created without
    /// permission. One shared tap serves all registered gestures (WPF
    /// single-hook parity): the first successful register
    /// creates/enables/attaches it and the last unregister
    /// detaches/disables it. Raw tap events are matched by keycode plus exact
    /// supported-modifier state (extra non-modifier flag bits ignored),
    /// autorepeat events are suppressed, and unregistered gestures are
    /// silent. A pre-cancelled <see cref="RegisterAsync"/> throws
    /// <see cref="OperationCanceledException"/> — a documented deviation from
    /// the fallback service, which returns Unsupported; the same applies to a
    /// pre-cancelled <see cref="UnregisterAsync"/>. After Dispose,
    /// <see cref="RegisterAsync"/> and <see cref="UnregisterAsync"/> throw
    /// <see cref="ObjectDisposedException"/>. The last unregister tears the
    /// shared tap down outside the registration lock (so a reentrant tap
    /// callback cannot deadlock), and a concurrent register negotiates with
    /// the in-flight teardown: it waits for the teardown to finish and then
    /// installs a fresh tap, so a registered gesture can never silently bind
    /// to a tap that is about to be torn down. Dispose is idempotent,
    /// detaches <see cref="HotkeyPressed"/>, stops the tap, and never
    /// deadlocks when a tap callback fires reentrantly during teardown.
    /// </summary>
    public sealed class MacGlobalHotkeyService : IGlobalHotkeyService
    {
        private readonly object _sync = new();
        private readonly IMacEventTap _eventTap;
        private readonly IHotkeyPermissionProbe _permissionProbe;
        private readonly Func<bool> _isMacOS;

        /// <summary>Registered gestures with their press/release latch.</summary>
        private readonly Dictionary<HotkeyGesture, bool> _gestures = new();

        private HotkeyPermissionStatus? _cachedPermission;
        private bool _tapActive;

        /// <summary>
        /// True while a last-unregister teardown is in flight. The teardown
        /// itself runs outside <c>_sync</c> (so a reentrant tap callback
        /// cannot deadlock); the flag is set and cleared under the lock, and
        /// <see cref="Monitor.PulseAll"/> wakes registers that negotiated
        /// with it.
        /// </summary>
        private bool _tearingDown;

        private volatile bool _disposed;

        /// <summary>
        /// Derives the host check from the runtime (<see cref="PlatformInfo.IsMacOS"/>).
        /// </summary>
        /// <param name="eventTap">The event tap driving key events; never
        /// touched on non-macOS hosts.</param>
        /// <param name="permissionProbe">The TCC permission probe; never
        /// queried on non-macOS hosts.</param>
        /// <exception cref="ArgumentNullException">When
        /// <paramref name="eventTap"/> or <paramref name="permissionProbe"/>
        /// is null.</exception>
        public MacGlobalHotkeyService(
            IMacEventTap eventTap,
            IHotkeyPermissionProbe permissionProbe)
            : this(eventTap, permissionProbe, () => PlatformInfo.IsMacOS)
        {
        }

        /// <summary>
        /// Uses the supplied host predicate, so the macOS check can be
        /// controlled (e.g. in tests).
        /// </summary>
        /// <param name="eventTap">The event tap driving key events; never
        /// touched when the predicate reports a non-macOS host.</param>
        /// <param name="permissionProbe">The TCC permission probe; never
        /// queried when the predicate reports a non-macOS host.</param>
        /// <param name="isMacOS">Host predicate returning true on macOS.</param>
        /// <exception cref="ArgumentNullException">When
        /// <paramref name="eventTap"/>, <paramref name="permissionProbe"/> or
        /// <paramref name="isMacOS"/> is null.</exception>
        public MacGlobalHotkeyService(
            IMacEventTap eventTap,
            IHotkeyPermissionProbe permissionProbe,
            Func<bool> isMacOS)
        {
            _eventTap = eventTap ?? throw new ArgumentNullException(nameof(eventTap));
            _permissionProbe = permissionProbe ?? throw new ArgumentNullException(nameof(permissionProbe));
            _isMacOS = isMacOS ?? throw new ArgumentNullException(nameof(isMacOS));

            // Subscribed once for the service lifetime: pre-registration and
            // unregistered events are silent because nothing matches, and
            // Dispose unhooks the handler before teardown.
            _eventTap.KeyEvent += OnKeyEvent;
        }

        /// <summary>
        /// Raised when a registered gesture fires: the first matching raw
        /// event reports <see cref="HotkeyEventType.Pressed"/>, the second
        /// <see cref="HotkeyEventType.Released"/>, alternating thereafter.
        /// Raised outside the registration lock, so tap callbacks can never
        /// deadlock against registration state.
        /// </summary>
        public event EventHandler<HotkeyEventArgs>? HotkeyPressed;

        /// <summary>
        /// Reports the capability of a gesture on this host. Off macOS the
        /// answer is always <see cref="HotkeyCapability.Unsupported"/> and
        /// neither the probe nor the tap is touched. On macOS the probe
        /// result is cached after the first query; a denied probe maps to
        /// <see cref="HotkeyCapability.PermissionRequired"/>, otherwise the
        /// gesture is <see cref="HotkeyCapability.Available"/> when its key
        /// maps to a kVK code and <see cref="HotkeyCapability.Unsupported"/>
        /// otherwise.
        /// </summary>
        public HotkeyCapability GetCapability(HotkeyGesture gesture)
        {
            if (!_isMacOS())
            {
                return HotkeyCapability.Unsupported;
            }

            var permission = GetPermission();
            if (permission == HotkeyPermissionStatus.AccessibilityRequired
                || permission == HotkeyPermissionStatus.InputMonitoringRequired)
            {
                return HotkeyCapability.PermissionRequired;
            }

            return MacHotkeyKeyCodes.TryGetKeyCode(gesture.Key, out _)
                ? HotkeyCapability.Available
                : HotkeyCapability.Unsupported;
        }

        /// <summary>
        /// Registers a gesture. A pre-cancelled token throws
        /// <see cref="OperationCanceledException"/> (documented deviation from
        /// the fallback service, which reports Unsupported). After Dispose,
        /// throws <see cref="ObjectDisposedException"/>. Off macOS the
        /// result is <see cref="HotkeyRegistrationStatus.Unsupported"/>
        /// preserving the gesture. Without permission the result is
        /// <see cref="HotkeyRegistrationStatus.PermissionDenied"/> preserving
        /// the gesture and the tap is never created. A duplicate registration
        /// reports <see cref="HotkeyRegistrationStatus.AlreadyRegistered"/>.
        /// The shared tap is created/enabled/attached exactly once across all
        /// gestures — on the first successful register; a failed
        /// <see cref="IMacEventTap.Create"/> reports
        /// <see cref="HotkeyRegistrationStatus.Unsupported"/>. A register
        /// issued while a last-unregister teardown is in flight waits for the
        /// teardown to finish and then installs a fresh tap, so it never
        /// binds to a tap that is about to be torn down.
        /// </summary>
        public Task<HotkeyRegistrationResult> RegisterAsync(
            HotkeyGesture gesture,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromException<HotkeyRegistrationResult>(
                    new OperationCanceledException(cancellationToken));
            }

            if (_disposed)
            {
                return Task.FromException<HotkeyRegistrationResult>(
                    new ObjectDisposedException(nameof(MacGlobalHotkeyService)));
            }

            if (!_isMacOS())
            {
                return Task.FromResult(
                    new HotkeyRegistrationResult(HotkeyRegistrationStatus.Unsupported, gesture));
            }

            var permission = GetPermission();
            if (permission != HotkeyPermissionStatus.Granted
                && permission != HotkeyPermissionStatus.NotApplicable)
            {
                return Task.FromResult(
                    new HotkeyRegistrationResult(HotkeyRegistrationStatus.PermissionDenied, gesture));
            }

            lock (_sync)
            {
                // Authoritative disposed check under the lock: Dispose may
                // have completed between the fast-path check above and here.
                if (_disposed)
                {
                    return Task.FromException<HotkeyRegistrationResult>(
                        new ObjectDisposedException(nameof(MacGlobalHotkeyService)));
                }

                // A last-unregister teardown runs outside this lock (so a
                // reentrant tap callback cannot deadlock). Wait for it to
                // finish before touching the tap: creating/attaching over the
                // stale tap would no-op or bind to a tap the teardown is
                // about to kill — a registered gesture that silently never
                // fires.
                while (_tearingDown)
                {
                    Monitor.Wait(_sync);
                }

                // Dispose may have run while we waited for the teardown.
                if (_disposed)
                {
                    return Task.FromException<HotkeyRegistrationResult>(
                        new ObjectDisposedException(nameof(MacGlobalHotkeyService)));
                }

                if (_gestures.ContainsKey(gesture))
                {
                    return Task.FromResult(
                        new HotkeyRegistrationResult(HotkeyRegistrationStatus.AlreadyRegistered, gesture));
                }

                if (!_tapActive)
                {
                    if (!_eventTap.Create())
                    {
                        return Task.FromResult(
                            new HotkeyRegistrationResult(HotkeyRegistrationStatus.Unsupported, gesture));
                    }

                    _eventTap.Enable();
                    _eventTap.AttachRunLoop();
                    _tapActive = true;
                }

                _gestures.Add(gesture, false);
            }

            return Task.FromResult(
                new HotkeyRegistrationResult(HotkeyRegistrationStatus.Registered, gesture));
        }

        /// <summary>
        /// Unregisters a gesture. A pre-cancelled token throws
        /// <see cref="OperationCanceledException"/> (documented deviation from
        /// the fallback service, which returns Unsupported; parity with
        /// <see cref="RegisterAsync"/>). After Dispose, throws
        /// <see cref="ObjectDisposedException"/>. Idempotent: unregistering a
        /// gesture that is not registered is a no-op. When the last
        /// registered gesture is removed the shared tap is detached and
        /// disabled outside the lock, so a later re-registration starts from
        /// a fresh tap; a concurrent register waits for that teardown to
        /// finish before creating one.
        /// </summary>
        public Task UnregisterAsync(HotkeyGesture gesture, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromException(
                    new OperationCanceledException(cancellationToken));
            }

            if (_disposed)
            {
                return Task.FromException(
                    new ObjectDisposedException(nameof(MacGlobalHotkeyService)));
            }

            var shouldTearDown = false;
            lock (_sync)
            {
                // Authoritative disposed check under the lock: Dispose may
                // have completed between the fast-path check above and here.
                if (_disposed)
                {
                    return Task.FromException(
                        new ObjectDisposedException(nameof(MacGlobalHotkeyService)));
                }

                if (_gestures.Remove(gesture) && _gestures.Count == 0 && _tapActive)
                {
                    _tapActive = false;

                    // Negotiated with concurrent registers: set under the
                    // lock before the teardown begins so a register cannot
                    // slip in and attach a fresh tap that the in-flight
                    // teardown would then kill.
                    _tearingDown = true;
                    shouldTearDown = true;
                }
            }

            if (shouldTearDown)
            {
                try
                {
                    // Teardown outside the lock: a tap callback raised
                    // reentrantly during DetachRunLoop must not deadlock
                    // against registration state or reach subscribers.
                    _eventTap.DetachRunLoop();
                    _eventTap.Disable();
                }
                finally
                {
                    // Release registers that negotiated with this teardown;
                    // they may now create a fresh tap.
                    lock (_sync)
                    {
                        _tearingDown = false;
                        Monitor.PulseAll(_sync);
                    }
                }
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Releases the service. Idempotent: detaches <see cref="HotkeyPressed"/>,
        /// unhooks the tap callback, detaches and disables the shared tap and
        /// disposes it. Reentrant tap callbacks raised during teardown are
        /// swallowed and never reach subscribers. After Dispose, any
        /// <see cref="RegisterAsync"/> or <see cref="UnregisterAsync"/> call
        /// throws <see cref="ObjectDisposedException"/>.
        /// </summary>
        public void Dispose()
        {
            var shouldTearDown = false;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                shouldTearDown = _tapActive;
                _tapActive = false;
            }

            // Detach the subscribers before teardown so a reentrant callback
            // fired by DetachRunLoop cannot reach them.
            HotkeyPressed = null;
            _eventTap.KeyEvent -= OnKeyEvent;

            if (shouldTearDown)
            {
                _eventTap.DetachRunLoop();
                _eventTap.Disable();
            }

            _eventTap.Dispose();
        }

        /*
        ** Helpers
        */

        /// <summary>
        /// Returns the probe result, cached after the first query so the
        /// permission state stays stable for the service lifetime.
        /// </summary>
        private HotkeyPermissionStatus GetPermission()
        {
            lock (_sync)
            {
                if (_cachedPermission is { } cached)
                {
                    return cached;
                }

                var result = _permissionProbe.Query();
                _cachedPermission = result;
                return result;
            }
        }

        /// <summary>
        /// Tap callback handler. Suppresses autorepeat events, maps the
        /// keycode and matches exact supported-modifier state (ignoring all
        /// non-modifier flag bits). Every raw event for a registered key
        /// toggles that gesture's press/release latch — parity with the WPF
        /// keyDown/keyUp latch — so a modifier-mismatched event still advances
        /// the state and the next matching event reports the correct
        /// transition. Events are raised outside the lock.
        /// </summary>
        private void OnKeyEvent(MacKeyEventData data)
        {
            if (_disposed || data.IsAutorepeat)
            {
                return;
            }

            if (!MacHotkeyKeyCodes.TryGetHotkeyKey(data.KeyCode, out var key))
            {
                return;
            }

            var modifiers = MacHotkeyKeyCodes.ToModifiers(data.Flags);

            HotkeyEventArgs? args = null;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                foreach (var gesture in _gestures.Keys)
                {
                    if (gesture.Key != key)
                    {
                        continue;
                    }

                    _gestures[gesture] = !_gestures[gesture];

                    if (modifiers == gesture.Modifiers)
                    {
                        args = new HotkeyEventArgs(
                            gesture,
                            _gestures[gesture]
                                ? HotkeyEventType.Pressed
                                : HotkeyEventType.Released);
                    }
                }
            }

            if (args is not null)
            {
                HotkeyPressed?.Invoke(this, args);
            }
        }
    }
}
