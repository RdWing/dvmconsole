// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace DvmConsole.Avalonia.ViewModels
{
    /// <summary>
    /// Pure managed view-model for the operator dashboard main window. The
    /// dashboard starts disconnected and awaiting FNE configuration, with
    /// exactly four fixed channel slots; connection state is replaced
    /// wholesale through <see cref="SetConnectionState"/>. This class is
    /// deliberately free of Avalonia, protocol, audio, and network
    /// behavior so it can be driven headlessly.
    /// </summary>
    public sealed class MainWindowViewModel : INotifyPropertyChanged
    {
        private const int ChannelCount = 4;

        /// <summary>The fixed product name shown by the dashboard.</summary>
        public string ProductName { get; } = "DVM Console";

        /// <summary>
        /// The connection state label, e.g. <c>OFFLINE</c> or
        /// <c>LINKED</c>. Set verbatim by <see cref="SetConnectionState"/>.
        /// </summary>
        public string ConnectionLabel { get; private set; } = "OFFLINE";

        /// <summary>
        /// The connection detail line, e.g.
        /// <c>Awaiting FNE configuration</c> or the FNE endpoint. Set
        /// verbatim by <see cref="SetConnectionState"/>.
        /// </summary>
        public string ConnectionDetail { get; private set; } = "Awaiting FNE configuration";

        /// <summary>True when the console is connected to the FNE.</summary>
        public bool IsConnected { get; private set; }

        /// <summary>True when the operator may initiate a connection.</summary>
        public bool CanConnect { get; private set; } = true;

        /// <summary>
        /// The fixed channel slots of the dashboard, numbered 1..4.
        /// Exposed read-only; the backing collection is never mutated
        /// after construction.
        /// </summary>
        public IReadOnlyList<ChannelSlotViewModel> Channels { get; }

        /// <summary>
        /// Raised whenever a connection-state property changes. All four
        /// properties are reported on every <see cref="SetConnectionState"/>
        /// call, in the locked order: ConnectionLabel, ConnectionDetail,
        /// IsConnected, CanConnect.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Creates the offline dashboard with exactly four channel slots.
        /// </summary>
        public MainWindowViewModel()
        {
            var channels = new ChannelSlotViewModel[ChannelCount];
            for (var i = 0; i < ChannelCount; i++)
            {
                var number = i + 1;
                channels[i] = new ChannelSlotViewModel(number, $"CHANNEL {number:00}");
            }

            Channels = channels;
        }

        /// <summary>
        /// Replaces the connection state wholesale. Nonblank label and
        /// detail strings are preserved verbatim, including surrounding
        /// whitespace; null or whitespace-only values are programming
        /// errors and are rejected with <see cref="ArgumentException"/>.
        /// Notifications are raised on every call, even when values are
        /// unchanged.
        /// </summary>
        public void SetConnectionState(string label, string detail, bool isConnected)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                throw new ArgumentException("Connection label must be nonblank.", nameof(label));
            }

            if (string.IsNullOrWhiteSpace(detail))
            {
                throw new ArgumentException("Connection detail must be nonblank.", nameof(detail));
            }

            ConnectionLabel = label;
            ConnectionDetail = detail;
            IsConnected = isConnected;
            CanConnect = !isConnected;

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionDetail)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsConnected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanConnect)));
        }
    }
}
