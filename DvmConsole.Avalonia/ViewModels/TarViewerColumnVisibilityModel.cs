// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using DvmConsole.Avalonia.Persistence;

namespace DvmConsole.Avalonia.ViewModels
{
    /// <summary>
    /// Stable descriptor for one TAR Viewer column. Visibility is mutable and
    /// notifies the existing Avalonia bindings; identity, label, and width are
    /// fixed by the WPF-parity default table.
    /// </summary>
    public sealed class TarViewerColumnDescriptor : INotifyPropertyChanged
    {
        private bool isVisible;

        public TarViewerColumnDescriptor(
            string key,
            string header,
            double width,
            bool defaultVisible)
        {
            Key = key ?? throw new ArgumentNullException(nameof(key));
            Header = header ?? throw new ArgumentNullException(nameof(header));
            Width = width;
            DefaultVisible = defaultVisible;
            isVisible = defaultVisible;
        }

        public string Key { get; }
        public string Header { get; }
        public double Width { get; }
        public bool DefaultVisible { get; }

        public bool IsVisible
        {
            get => isVisible;
            set
            {
                if (isVisible == value)
                    return;

                isVisible = value;
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(IsVisible)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// Headless TAR Viewer column model. It owns WPF-parity defaults, ignores
    /// unknown persisted or toggle keys, and saves the complete known-key map
    /// through the injected merge-preserving adapter.
    /// </summary>
    public sealed class TarViewerColumnVisibilityModel
    {
        private readonly TarViewerColumnSettingsPersistence? persistence;

        public TarViewerColumnVisibilityModel(
            TarViewerColumnSettingsPersistence? persistence = null)
        {
            this.persistence = persistence;
            Columns = CreateDefaults();

            if (persistence is not null
                && persistence.TryLoad(out var section))
            {
                foreach (TarViewerColumnDescriptor column in Columns)
                {
                    if (section.ColumnVisibility.TryGetValue(column.Key, out bool visible))
                        column.IsVisible = visible;
                }
            }
        }

        public IReadOnlyList<TarViewerColumnDescriptor> Columns { get; }

        /// <summary>
        /// Sets one known column's visibility. Unknown keys are a no-op and
        /// return false; known keys return true even when already at the value.
        /// </summary>
        public bool TrySetVisibility(string key, bool visible)
        {
            if (string.IsNullOrWhiteSpace(key))
                return false;

            TarViewerColumnDescriptor? column = Columns.FirstOrDefault(
                candidate => string.Equals(candidate.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));
            if (column is null)
                return false;

            column.IsVisible = visible;
            return true;
        }

        /// <summary>Saves all known column visibility values.</summary>
        public void Save()
        {
            persistence?.Save(Columns.ToDictionary(
                column => column.Key,
                column => column.IsVisible,
                StringComparer.OrdinalIgnoreCase));
        }

        private static List<TarViewerColumnDescriptor> CreateDefaults()
            => new List<TarViewerColumnDescriptor>
            {
                new TarViewerColumnDescriptor("Time", "Time", 155, true),
                new TarViewerColumnDescriptor("Duration", "Duration", 90, true),
                new TarViewerColumnDescriptor("Channel", "Channel", 190, true),
                new TarViewerColumnDescriptor("Talkgroup", "TG", 90, true),
                new TarViewerColumnDescriptor("SourceId", "Source ID", 90, true),
                new TarViewerColumnDescriptor("Alias", "Alias", 160, true),
                new TarViewerColumnDescriptor("Direction", "Dir", 60, false),
                new TarViewerColumnDescriptor("Protocol", "Protocol", 85, false),
                new TarViewerColumnDescriptor("System", "System", 140, false),
                new TarViewerColumnDescriptor("Encryption", "Enc", 120, false),
            };
    }
}
