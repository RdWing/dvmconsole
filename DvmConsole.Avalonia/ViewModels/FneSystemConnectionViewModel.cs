// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.ComponentModel;
using dvmconsole;

namespace DvmConsole.Avalonia.ViewModels
{
    /// <summary>
    /// Observable view-model row for one codeplug FNE system. The identity
    /// and connection configuration (<see cref="SystemName"/>,
    /// <see cref="Identity"/>, <see cref="Address"/>, <see cref="Port"/>,
    /// <see cref="Encrypted"/>, <see cref="PeerId"/>, and the derived
    /// <see cref="Endpoint"/>) is projected read-only from the wrapped
    /// <see cref="Codeplug.System"/> at construction. Authentication
    /// secrets (password, preshared key, radio ID) are deliberately never
    /// copied onto this surface. The live flags
    /// <see cref="IsConnected"/>, <see cref="IsBusy"/>, and
    /// <see cref="IsStarted"/> are writable and observable, and the
    /// derived <see cref="StatusText"/>, <see cref="ToggleButtonText"/>,
    /// and <see cref="ButtonsEnabled"/> follow them. This class is
    /// deliberately free of network and protocol behavior.
    /// </summary>
    public sealed class FneSystemConnectionViewModel : INotifyPropertyChanged
    {
        private bool isConnected;
        private bool isBusy;
        private bool isStarted;

        /// <summary>
        /// Projects the given codeplug system onto an observable row. The
        /// wrapped system must not be null.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="system"/> is null.
        /// </exception>
        public FneSystemConnectionViewModel(Codeplug.System system)
        {
            if (system is null)
            {
                throw new ArgumentNullException(nameof(system));
            }

            SystemName = system.Name;
            Identity = system.Identity;
            Address = system.Address;
            Port = system.Port;
            Encrypted = system.Encrypted;
            PeerId = system.PeerId;
        }

        /// <summary>The configured system name.</summary>
        public string SystemName { get; }

        /// <summary>The identity string reported to the FNE.</summary>
        public string Identity { get; }

        /// <summary>
        /// The configured FNE IP address or hostname, preserved verbatim.
        /// </summary>
        public string Address { get; }

        /// <summary>The configured FNE port number.</summary>
        public int Port { get; }

        /// <summary>True when the FNE connection is encrypted.</summary>
        public bool Encrypted { get; }

        /// <summary>The configured unique peer ID.</summary>
        public uint PeerId { get; }

        /// <summary>
        /// The endpoint text, <see cref="Address"/> and <see cref="Port"/>
        /// concatenated with a colon. The address text is preserved
        /// verbatim, including any surrounding whitespace.
        /// </summary>
        public string Endpoint => Address + ":" + Port;

        /// <summary>
        /// <c>Connected</c> while <see cref="IsConnected"/> is true,
        /// otherwise <c>Disconnected</c>.
        /// </summary>
        public string StatusText => IsConnected ? "Connected" : "Disconnected";

        /// <summary>
        /// <c>Stop</c> while <see cref="IsConnected"/> is true, otherwise
        /// <c>Start</c>.
        /// </summary>
        public string ToggleButtonText => IsConnected ? "Stop" : "Start";

        /// <summary>True when the row is not busy; false while a request is outstanding.</summary>
        public bool ButtonsEnabled => !IsBusy;

        /// <summary>
        /// True when the row reports a live FNE connection. Raising this
        /// notifies <c>IsConnected</c>, <c>StatusText</c>, and
        /// <c>ToggleButtonText</c>, in that order, and only on actual
        /// change.
        /// </summary>
        public bool IsConnected
        {
            get => isConnected;
            set
            {
                if (isConnected == value)
                {
                    return;
                }

                isConnected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsConnected)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToggleButtonText)));
            }
        }

        /// <summary>
        /// True while a start/stop/restart request is outstanding. Raising
        /// this notifies <c>IsBusy</c> and <c>ButtonsEnabled</c>, in that
        /// order, and only on actual change.
        /// </summary>
        public bool IsBusy
        {
            get => isBusy;
            set
            {
                if (isBusy == value)
                {
                    return;
                }

                isBusy = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBusy)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ButtonsEnabled)));
            }
        }

        /// <summary>
        /// True when the system has been started. Raises
        /// <c>IsStarted</c> only, and only on actual change.
        /// </summary>
        public bool IsStarted
        {
            get => isStarted;
            set
            {
                if (isStarted == value)
                {
                    return;
                }

                isStarted = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsStarted)));
            }
        }

        /// <summary>Raised when any observable property changes.</summary>
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
