// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Platform.Hotkeys;

namespace DvmConsole.Avalonia.Hotkeys
{
    /// <summary>
    /// Owns the bridge between the PTT capability view-model's hotkey
    /// requests and the platform global-hotkey service: it is the seam
    /// that actually registers and unregisters the configured PTT hotkey
    /// gesture on the service (nothing else calls
    /// <see cref="IGlobalHotkeyService.RegisterAsync"/> /
    /// <see cref="IGlobalHotkeyService.UnregisterAsync"/>).
    ///
    /// The coordinator subscribes to
    /// <see cref="PttCapabilityViewModel.HotkeyChangeRequested"/> for
    /// its whole lifetime and reconciles each request onto the service
    /// through a single-flight, fire-and-forget background loop: at most
    /// one register/unregister operation is pending or in flight at any
    /// time (<see cref="Idle"/> reports that state), and requests are
    /// coalesced, never queued unboundedly. It references the
    /// view-model only through its get-only <see cref="PttCapabilityViewModel.Hotkey"/>
    /// property and the change-request event; it is headless and
    /// deterministic — no timers, no dispatcher, no UI, no thread
    /// affinity.
    ///
    /// Reconciliation order: a new gesture unregisters the previously
    /// registered gesture first (if any) and then registers the new one,
    /// in that order; a null request (clear) only unregisters; a request
    /// equal to the gesture currently tracked as registered is a
    /// duplicate and makes no service call (the view-model raises the
    /// event even when the gesture is unchanged). Duplicate events must
    /// never re-register.
    ///
    /// Error policy: the synchronous event handler never throws; any
    /// exception thrown by RegisterAsync/UnregisterAsync is caught and
    /// dropped — registration failures (exceptions or a
    /// <see cref="HotkeyRegistrationStatus.PermissionDenied"/> /
    /// <see cref="HotkeyRegistrationStatus.Unsupported"/> outcome) are
    /// non-fatal and never retried. The tracked-gesture mirror updates
    /// to the last REQUESTED gesture regardless of the registration
    /// outcome, so a duplicate request for the same gesture never
    /// re-registers — a retry is never automatic.
    ///
    /// Ownership contract (RED-pinned by <c>HotkeyRegistrationCoordinatorTests</c>,
    /// <c>Coordinator_Dispose_UnregistersAndDetaches</c>): the
    /// coordinator owns registration state, not the service —
    /// <see cref="Dispose"/> unregisters the tracked gesture exactly
    /// once (if any) and never disposes the injected
    /// <see cref="IGlobalHotkeyService"/>. The App owns the service's
    /// lifetime and disposes it separately (App.axaml.cs: "disposal is
    /// handled by a later concrete factory/lifecycle slice").
    /// </summary>
    public sealed class HotkeyRegistrationCoordinator : IDisposable
    {
        private readonly IGlobalHotkeyService hotkeys;
        private readonly PttCapabilityViewModel ptt;
        private readonly object gate = new();

        /// <summary>The gesture currently registered on the service, or null.</summary>
        private HotkeyGesture? registered;

        /// <summary>The latest requested gesture awaiting reconciliation; null means "clear".</summary>
        private HotkeyGesture? pending;

        /// <summary>True while <see cref="pending"/> holds an unreconciled request.</summary>
        private bool hasPending;

        /// <summary>True while the fire-and-forget reconciliation loop is running.</summary>
        private bool reconciling;

        private bool disposed;

        /// <summary>
        /// Creates the coordinator, subscribes to
        /// <see cref="PttCapabilityViewModel.HotkeyChangeRequested"/>, and — when a
        /// hotkey is already configured on the view-model — immediately reconciles
        /// that gesture onto the service once.
        /// </summary>
        /// <param name="hotkeys">The platform global-hotkey service to register and unregister on; borrowed, not owned — the App disposes it (see class doc).</param>
        /// <param name="ptt">The PTT capability view-model to observe.</param>
        /// <exception cref="ArgumentNullException">When either argument is null.</exception>
        public HotkeyRegistrationCoordinator(
            IGlobalHotkeyService hotkeys,
            PttCapabilityViewModel ptt)
        {
            this.hotkeys = hotkeys ?? throw new ArgumentNullException(nameof(hotkeys));
            this.ptt = ptt ?? throw new ArgumentNullException(nameof(ptt));

            ptt.HotkeyChangeRequested += OnHotkeyChangeRequested;

            if (ptt.Hotkey is { } existing)
            {
                Enqueue(existing);
            }
        }

        /// <summary>
        /// True when no reconciliation operation is pending or in flight: the
        /// tracked gesture, if any, matches the service.
        /// </summary>
        public bool Idle
        {
            get
            {
                lock (gate)
                {
                    return !reconciling && !hasPending;
                }
            }
        }

        /// <summary>
        /// Detaches from <see cref="PttCapabilityViewModel.HotkeyChangeRequested"/>
        /// and unregisters the tracked gesture (if any) exactly once, without
        /// disposing the injected <see cref="IGlobalHotkeyService"/>; later
        /// SetHotkey/ClearHotkey calls make no service calls. Idempotent and never
        /// throws, and an in-flight reconciliation makes no further service calls
        /// once disposal has landed.
        /// </summary>
        public void Dispose()
        {
            HotkeyGesture? tracked;

            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                ptt.HotkeyChangeRequested -= OnHotkeyChangeRequested;
                pending = null;
                hasPending = false;
                tracked = registered;
                registered = null;
            }

            if (tracked is { } gesture)
            {
                try
                {
                    // Unregister the tracked gesture exactly once and wait for
                    // the operation to complete, so teardown is finished when
                    // Dispose returns. The injected service is NOT disposed
                    // here — the App owns its lifetime (see class doc).
                    hotkeys.UnregisterAsync(gesture, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                }
                catch
                {
                    // Documented: teardown must never propagate out of the
                    // coordinator's Dispose.
                }
            }
        }

        private void OnHotkeyChangeRequested(HotkeyGesture? gesture)
        {
            try
            {
                lock (gate)
                {
                    if (disposed)
                    {
                        return;
                    }

                    // Same gesture as the one currently registered (or already
                    // queued) is a duplicate request: the view-model raises the
                    // event even when the gesture is unchanged, and duplicates
                    // must not re-register.
                    if (gesture == registered || (hasPending && gesture == pending))
                    {
                        return;
                    }

                    EnqueueLocked(gesture);
                }
            }
            catch
            {
                // Documented: the event handler never throws.
            }
        }

        private void Enqueue(HotkeyGesture? gesture)
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                EnqueueLocked(gesture);
            }
        }

        /// <summary>Callers must hold <see cref="gate"/>.</summary>
        private void EnqueueLocked(HotkeyGesture? gesture)
        {
            pending = gesture;
            hasPending = true;

            if (reconciling)
            {
                // The in-flight loop picks the coalesced target up on its next
                // iteration; requests coalesce, never queue unboundedly.
                return;
            }

            reconciling = true;
            _ = ReconcileAsync();
        }

        private async Task ReconcileAsync()
        {
            while (true)
            {
                HotkeyGesture? target;

                lock (gate)
                {
                    if (disposed || !hasPending)
                    {
                        reconciling = false;
                        return;
                    }

                    target = pending;
                    pending = null;
                    hasPending = false;
                }

                try
                {
                    await ReconcileCoreAsync(target);
                }
                catch
                {
                    // Documented: registration/unregistration failures are
                    // non-fatal and must not retry; the exception is dropped.
                }

                lock (gate)
                {
                    if (disposed || !hasPending)
                    {
                        reconciling = false;
                        return;
                    }
                }
            }
        }

        private async Task ReconcileCoreAsync(HotkeyGesture? target)
        {
            HotkeyGesture? previous;

            lock (gate)
            {
                previous = registered;
            }

            // Unregister the previously registered gesture first, then register
            // the new one — in that order. Nothing to unregister when no gesture
            // was ever registered.
            if (previous is { } oldGesture && oldGesture != target)
            {
                Task unregisterTask;

                lock (gate)
                {
                    // The disposed check and the service invocation are atomic
                    // with respect to Dispose: a service call can only start
                    // before disposal lands, never after.
                    if (disposed)
                    {
                        return;
                    }

                    unregisterTask = hotkeys.UnregisterAsync(oldGesture, CancellationToken.None);

                    // The mirror drops the gesture the moment the unregister is
                    // issued, regardless of the outcome, so a Dispose that lands
                    // while this call is in flight never unregisters a gesture
                    // this reconciliation already released, and a duplicate
                    // request can never re-register it.
                    registered = null;
                }

                try
                {
                    await unregisterTask;
                }
                catch
                {
                    // Documented: unregistration failures are non-fatal; the
                    // exception is dropped and the reconciliation continues.
                }
            }

            if (target is { } newGesture && newGesture != previous)
            {
                Task registerTask;

                lock (gate)
                {
                    // Same atomicity as the unregister path above.
                    if (disposed)
                    {
                        return;
                    }

                    registerTask = hotkeys.RegisterAsync(newGesture, CancellationToken.None);

                    // The mirror tracks the last requested gesture from the
                    // moment its registration is issued, regardless of the
                    // outcome, so a duplicate request for the same gesture
                    // never re-registers — a retry is never automatic — and a
                    // Dispose that lands while this call is in flight
                    // unregisters exactly the gesture being registered, never
                    // orphaning it on the service.
                    registered = newGesture;
                }

                try
                {
                    await registerTask;
                }
                catch
                {
                    // Documented: registration failures are non-fatal; the
                    // exception is dropped and the attempt is never retried.
                }
            }
        }
    }
}
