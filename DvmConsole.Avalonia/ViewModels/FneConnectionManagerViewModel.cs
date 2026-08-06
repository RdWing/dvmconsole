// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using dvmconsole;

namespace DvmConsole.Avalonia.ViewModels
{
    /// <summary>
    /// Observable manager for the FNE system connection rows of the
    /// operator console. Rows are built once from an
    /// <see cref="IReadOnlyList{T}"/> of <see cref="Codeplug.System"/>
    /// configs: entries with null, empty, or whitespace-only names are
    /// skipped, case-insensitive duplicate names collapse with the last
    /// config winning, and rows sort by <see cref="SystemName"/> with
    /// <see cref="StringComparer.OrdinalIgnoreCase"/>. The manager is a
    /// pure request-forwarding slice: <see cref="StartSystem"/>,
    /// <see cref="StopSystem"/>, and <see cref="RestartSystem"/> mark the
    /// matching row busy and raise the corresponding
    /// <c>Action&lt;string&gt;</c> event with the canonical row name, and
    /// <see cref="ApplyState"/> lands externally observed state verbatim.
    /// There is no automatic connection, network, or protocol behavior
    /// anywhere in this class.
    /// </summary>
    public sealed class FneConnectionManagerViewModel : INotifyPropertyChanged
    {
        private readonly IReadOnlyList<FneSystemConnectionViewModel> systems;

        private bool anyConnected;
        private string? connectedSystemSummary;

        /// <summary>
        /// Creates an empty manager: no rows, nothing connected.
        /// </summary>
        public FneConnectionManagerViewModel()
            : this(null)
        {
        }

        /// <summary>
        /// Builds the sorted, deduplicated row set from the given codeplug
        /// systems. A null list is treated as an empty list.
        /// </summary>
        public FneConnectionManagerViewModel(IReadOnlyList<Codeplug.System?>? systems)
        {
            if (systems is null)
            {
                this.systems = Array.Empty<FneSystemConnectionViewModel>();
                return;
            }

            var byName = new Dictionary<string, FneSystemConnectionViewModel>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var system in systems)
            {
                if (system is null || string.IsNullOrWhiteSpace(system.Name))
                {
                    continue;
                }

                byName[system.Name] = new FneSystemConnectionViewModel(system);
            }

            var rows = new List<FneSystemConnectionViewModel>(byName.Values);
            rows.Sort((left, right) =>
                StringComparer.OrdinalIgnoreCase.Compare(left.SystemName, right.SystemName));

            this.systems = rows.AsReadOnly();
        }

        /// <summary>
        /// The connection rows, sorted by system name. Read-only and
        /// fixed after construction.
        /// </summary>
        public IReadOnlyList<FneSystemConnectionViewModel> Systems => systems;

        /// <summary>True when at least one system row exists.</summary>
        public bool HasSystems => systems.Count > 0;

        /// <summary>True when no system rows exist.</summary>
        public bool HasNoSystems => systems.Count == 0;

        /// <summary>True when at least one row reports a live connection.</summary>
        public bool AnyConnected => anyConnected;

        /// <summary>
        /// The first connected row in sorted order, formatted
        /// <c>SystemName Endpoint</c>, or null when nothing is connected.
        /// </summary>
        public string? ConnectedSystemSummary => connectedSystemSummary;

        /// <summary>
        /// Raised when a start request is forwarded. The payload is the
        /// canonical row <see cref="FneSystemConnectionViewModel.SystemName"/>.
        /// </summary>
        public event Action<string>? StartRequested;

        /// <summary>
        /// Raised when a stop request is forwarded. The payload is the
        /// canonical row <see cref="FneSystemConnectionViewModel.SystemName"/>.
        /// </summary>
        public event Action<string>? StopRequested;

        /// <summary>
        /// Raised when a restart request is forwarded. The payload is the
        /// canonical row <see cref="FneSystemConnectionViewModel.SystemName"/>.
        /// </summary>
        public event Action<string>? RestartRequested;

        /// <summary>Raised when <see cref="AnyConnected"/> or <see cref="ConnectedSystemSummary"/> changes.</summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Applies externally observed connection state to the row whose
        /// name matches <paramref name="systemName"/>
        /// case-insensitively. Unknown or blank names are a no-op. The
        /// row flags are assigned verbatim, in the order IsConnected,
        /// IsBusy, IsStarted (so a false isBusy clears a busy row), each
        /// with the row's own change-only notifications, and the manager
        /// aggregates <see cref="AnyConnected"/> and
        /// <see cref="ConnectedSystemSummary"/> are refreshed with
        /// change-only notifications.
        /// </summary>
        public void ApplyState(string systemName, bool isConnected, bool isBusy, bool isStarted)
        {
            var row = FindRow(systemName);
            if (row is null)
            {
                return;
            }

            row.IsConnected = isConnected;
            row.IsBusy = isBusy;
            row.IsStarted = isStarted;

            UpdateConnectedAggregates();
        }

        /// <summary>
        /// Marks the matching row busy and raises
        /// <see cref="StartRequested"/> exactly once with the canonical
        /// row name. A no-op for unknown, blank, or busy rows.
        /// </summary>
        public void StartSystem(string systemName)
        {
            var row = FindRow(systemName);
            if (row is null || row.IsBusy)
            {
                return;
            }

            row.IsBusy = true;
            StartRequested?.Invoke(row.SystemName);
        }

        /// <summary>
        /// Marks the matching row busy and raises
        /// <see cref="StopRequested"/> exactly once with the canonical
        /// row name. A no-op for unknown, blank, or busy rows.
        /// </summary>
        public void StopSystem(string systemName)
        {
            var row = FindRow(systemName);
            if (row is null || row.IsBusy)
            {
                return;
            }

            row.IsBusy = true;
            StopRequested?.Invoke(row.SystemName);
        }

        /// <summary>
        /// Marks the matching row busy and raises
        /// <see cref="RestartRequested"/> exactly once with the canonical
        /// row name. A no-op for unknown, blank, or busy rows.
        /// </summary>
        public void RestartSystem(string systemName)
        {
            var row = FindRow(systemName);
            if (row is null || row.IsBusy)
            {
                return;
            }

            row.IsBusy = true;
            RestartRequested?.Invoke(row.SystemName);
        }

        private FneSystemConnectionViewModel? FindRow(string? systemName)
        {
            if (string.IsNullOrWhiteSpace(systemName))
            {
                return null;
            }

            foreach (var row in systems)
            {
                if (string.Equals(row.SystemName, systemName, StringComparison.OrdinalIgnoreCase))
                {
                    return row;
                }
            }

            return null;
        }

        private void UpdateConnectedAggregates()
        {
            var summary = ComputeConnectedSystemSummary();
            var anyConnected = summary is not null;
            var anyConnectedChanged = anyConnected != this.anyConnected;
            var summaryChanged = !string.Equals(
                summary,
                connectedSystemSummary,
                StringComparison.Ordinal);

            this.anyConnected = anyConnected;
            connectedSystemSummary = summary;

            if (anyConnectedChanged)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AnyConnected)));
            }

            if (summaryChanged)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectedSystemSummary)));
            }
        }

        private string? ComputeConnectedSystemSummary()
        {
            foreach (var row in systems)
            {
                if (row.IsConnected)
                {
                    return row.SystemName + " " + row.Endpoint;
                }
            }

            return null;
        }
    }
}
