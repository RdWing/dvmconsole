// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using Avalonia.Threading;
using DvmConsole.Avalonia.ViewModels;
using DvmConsole.Core.Networking;

namespace DvmConsole.Avalonia.Services
{
    /// <summary>
    /// The one platform-neutral event adapter between the shell's FNE
    /// connection manager and the headless connection service: it
    /// forwards the manager's
    /// <see cref="FneConnectionManagerViewModel.StartRequested"/>,
    /// <see cref="FneConnectionManagerViewModel.StopRequested"/>, and
    /// <see cref="FneConnectionManagerViewModel.RestartRequested"/>
    /// events into the injected <see cref="IFneConnectionService"/>, and
    /// marshals every service <see cref="IFneConnectionService.StateChanged"/>
    /// snapshot back into the manager through an injected UI-post
    /// delegate (default <see cref="DefaultUiPost"/>). Attach wires both
    /// directions; Detach (and Dispose) unwire them so no event can
    /// reach the manager or service afterwards.
    /// </summary>
    public sealed class FneConnectionServiceBridge : IDisposable
    {
        /// <summary>
        /// The default UI-thread marshalling delegate: posts onto the
        /// Avalonia UI dispatcher, exactly like the shell's other
        /// cross-thread callbacks.
        /// </summary>
        public static readonly Action<Action> DefaultUiPost = action => Dispatcher.UIThread.Post(action);

        private readonly IFneConnectionService service;
        private readonly FneConnectionManagerViewModel manager;
        private readonly Action<Action> uiPost;
        private readonly Action<FneConnectionSnapshot> onStateChanged;

        private bool attached;

        /// <summary>
        /// Creates the bridge. The injected post delegate (or the
        /// default UI-dispatcher post when null) is used to marshal
        /// service state changes onto the UI thread.
        /// </summary>
        /// <param name="service">The connection service; must not be null.</param>
        /// <param name="manager">The connection manager; must not be null.</param>
        /// <param name="uiPost">UI-post delegate, or null for the default.</param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="service"/> or <paramref name="manager"/> is null.
        /// </exception>
        public FneConnectionServiceBridge(
            IFneConnectionService service,
            FneConnectionManagerViewModel manager,
            Action<Action>? uiPost = null)
        {
            this.service = service ?? throw new ArgumentNullException(nameof(service));
            this.manager = manager ?? throw new ArgumentNullException(nameof(manager));
            this.uiPost = uiPost ?? DefaultUiPost;
            onStateChanged = OnStateChanged;
        }

        /// <summary>
        /// Wires both directions: manager requests forward into the
        /// service, service state changes marshal back onto the manager.
        /// Idempotent: a second Attach while already attached is a no-op.
        /// </summary>
        public void Attach()
        {
            if (attached)
            {
                return;
            }

            attached = true;

            manager.StartRequested += service.Start;
            manager.StopRequested += service.Stop;
            manager.RestartRequested += service.Restart;
            service.StateChanged += onStateChanged;
        }

        /// <summary>
        /// Unwires everything attached by <see cref="Attach"/>, in both
        /// directions. Idempotent.
        /// </summary>
        public void Detach()
        {
            if (!attached)
            {
                return;
            }

            attached = false;

            manager.StartRequested -= service.Start;
            manager.StopRequested -= service.Stop;
            manager.RestartRequested -= service.Restart;
            service.StateChanged -= onStateChanged;
        }

        /// <summary>
        /// Detaches the bridge. Idempotent.
        /// </summary>
        public void Dispose() => Detach();

        /// <summary>
        /// Marshals one service snapshot onto the UI thread and applies
        /// it to the manager verbatim.
        /// </summary>
        private void OnStateChanged(FneConnectionSnapshot snapshot)
            => uiPost(() => manager.ApplyState(
                snapshot.SystemName,
                snapshot.IsConnected,
                snapshot.IsBusy,
                snapshot.IsStarted));
    }
}
