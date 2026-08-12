// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using dvmconsole;

namespace DvmConsole.Avalonia.ViewModels
{
    /// <summary>
    /// Headless managed state for the custom alert-tone manager. It owns row
    /// normalization and request payloads; file dialogs, persistence, and
    /// confirmation remain shell-owned.
    /// </summary>
    public sealed class AlertToneManagerViewModel
    {
        private readonly Func<string, bool> isFileAvailable;

        public AlertToneManagerViewModel(
            IEnumerable<UserSettingsAlertToneConfig>? configs,
            IEnumerable<string>? availableTabs,
            Func<string, bool>? isFileAvailable = null)
        {
            this.isFileAvailable = isFileAvailable ?? (_ => true);
            AvailableTabs = NormalizeTabs(availableTabs);
            AlertTones = new ObservableCollection<AlertToneItem>(
                (configs ?? Enumerable.Empty<UserSettingsAlertToneConfig>())
                    .Where(config => config is not null
                        && !string.IsNullOrWhiteSpace(config.FilePath))
                    .Select(Normalize));
        }

        public ObservableCollection<AlertToneItem> AlertTones { get; }

        public IReadOnlyList<string> AvailableTabs { get; }

        /// <summary>
        /// Raised with a detached, normalized snapshot. No persistence is
        /// performed here.
        /// </summary>
        public event Action<IReadOnlyList<UserSettingsAlertToneConfig>>? SaveRequested;

        public void AddFiles(IEnumerable<string>? paths)
        {
            var existing = new HashSet<string>(
                AlertTones
                    .Select(item => item.FilePath)
                    .Where(path => !string.IsNullOrWhiteSpace(path)),
                StringComparer.OrdinalIgnoreCase);

            foreach (string? rawPath in paths ?? Enumerable.Empty<string>())
            {
                string path = rawPath?.Trim() ?? string.Empty;
                if (path.Length == 0 || !existing.Add(path))
                    continue;

                AlertTones.Add(new AlertToneItem(
                    Guid.NewGuid().ToString("N"),
                    FileDisplayName(path),
                    path,
                    AvailableTabs[0],
                    new UserSettingsLayoutPosition { X = 20, Y = 20 },
                    IsAvailable(path, isFileAvailable)));
            }
        }

        public void ReplaceFile(AlertToneItem? item, string? path)
        {
            if (item is null)
                return;

            string replacement = path?.Trim() ?? string.Empty;
            if (replacement.Length == 0)
                return;

            item.FilePath = replacement;
            if (string.IsNullOrWhiteSpace(item.DisplayName))
                item.DisplayName = FileDisplayName(replacement);
            item.IsAvailable = IsAvailable(replacement, isFileAvailable);
        }

        public void Delete(AlertToneItem? item)
        {
            if (item is not null)
                AlertTones.Remove(item);
        }

        public void Commit()
        {
            var snapshot = AlertTones
                .Where(item => !string.IsNullOrWhiteSpace(item.FilePath))
                .Select(item => item.ToConfig(AvailableTabs[0]))
                .ToList();
            SaveRequested?.Invoke(snapshot);
        }

        private AlertToneItem Normalize(UserSettingsAlertToneConfig config)
        {
            string path = config.FilePath.Trim();
            string tab = string.IsNullOrWhiteSpace(config.TabName)
                ? AvailableTabs[0]
                : config.TabName.Trim();
            string displayName = string.IsNullOrWhiteSpace(config.DisplayName)
                ? FileDisplayName(path)
                : config.DisplayName.Trim();
            var position = config.Position is null
                ? new UserSettingsLayoutPosition { X = 20, Y = 20 }
                : new UserSettingsLayoutPosition
                {
                    X = config.Position.X,
                    Y = config.Position.Y,
                };

            return new AlertToneItem(
                string.IsNullOrWhiteSpace(config.Id)
                    ? Guid.NewGuid().ToString("N")
                    : config.Id,
                displayName,
                path,
                tab,
                position,
                IsAvailable(path, isFileAvailable));
        }

        private static IReadOnlyList<string> NormalizeTabs(IEnumerable<string>? tabs)
        {
            var result = (tabs ?? Enumerable.Empty<string>())
                .Where(tab => !string.IsNullOrWhiteSpace(tab))
                .Select(tab => tab.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (result.Count == 0)
                result.Add("Tab 1");
            return result;
        }

        private static bool IsAvailable(string path, Func<string, bool> predicate)
        {
            try
            {
                return predicate(path);
            }
            catch
            {
                return false;
            }
        }

        private static string FileDisplayName(string path)
        {
            string name = path.Replace('\\', '/');
            int separator = name.LastIndexOf('/');
            if (separator >= 0)
                name = name[(separator + 1)..];
            int extension = name.LastIndexOf('.');
            if (extension > 0)
                name = name[..extension];
            return name.Length == 0 ? "Alert Tone" : name;
        }

        public sealed class AlertToneItem : INotifyPropertyChanged
        {
            private string displayName;
            private string filePath;
            private string tabName;
            private bool isAvailable;

            internal AlertToneItem(
                string id,
                string displayName,
                string filePath,
                string tabName,
                UserSettingsLayoutPosition position,
                bool isAvailable)
            {
                Id = id;
                this.displayName = displayName;
                this.filePath = filePath;
                this.tabName = tabName;
                Position = position;
                this.isAvailable = isAvailable;
            }

            public string Id { get; }

            public string DisplayName
            {
                get => displayName;
                set => Set(ref displayName, value ?? string.Empty, nameof(DisplayName));
            }

            public string FilePath
            {
                get => filePath;
                set => Set(ref filePath, value ?? string.Empty, nameof(FilePath));
            }

            public string TabName
            {
                get => tabName;
                set => Set(ref tabName, value ?? string.Empty, nameof(TabName));
            }

            public UserSettingsLayoutPosition Position { get; }

            public bool IsAvailable
            {
                get => isAvailable;
                internal set => Set(ref isAvailable, value, nameof(IsAvailable));
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            internal UserSettingsAlertToneConfig ToConfig(string fallbackTab)
                => new()
                {
                    Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id,
                    DisplayName = string.IsNullOrWhiteSpace(DisplayName)
                        ? FileDisplayName(FilePath)
                        : DisplayName.Trim(),
                    FilePath = FilePath.Trim(),
                    TabName = string.IsNullOrWhiteSpace(TabName) ? fallbackTab : TabName.Trim(),
                    Position = new UserSettingsLayoutPosition
                    {
                        X = Position.X,
                        Y = Position.Y,
                    },
                };

            private void Set<T>(ref T field, T value, string propertyName)
            {
                if (EqualityComparer<T>.Default.Equals(field, value))
                    return;
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}
