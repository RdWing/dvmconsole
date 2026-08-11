// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using dvmconsole;

namespace DvmConsole.Avalonia.ViewModels
{
    /// <summary>
    /// Headless TAR recording list/view-model for the Avalonia shell: a
    /// WPF-compatible projection of persisted recordings into display rows plus
    /// the TAR viewer's search/field/date filter semantics, ported from the WPF
    /// <c>TarViewerWindow</c> oracle with no Avalonia controls, dispatcher,
    /// async refresh, playback, file reveal, confirmation dialogs, deletion,
    /// settings, or MainWindow references.
    /// The shell window owns those event and platform-service seams; the remaining
    /// viewer work is column-visibility behavior and MainWindow/menu composition.
    /// </summary>
    public sealed class TarViewerViewModel : INotifyPropertyChanged
    {
        /// <summary>
        /// One display row for a persisted TAR recording. Projections mirror the
        /// WPF <c>TarViewerWindow.TarRecordingListItem</c> oracle exactly, with
        /// the task contract's null-to-empty string normalizations.
        /// </summary>
        public sealed class TarRecordingListItem
        {
            /// <summary>Full metadata loaded from the recording sidecar.</summary>
            public TarRecordingMetadata Metadata { get; init; } = null!;

            /// <summary>UTC start instant; the WPF grid's descending sort key.</summary>
            public DateTime UtcStartSortKey => Metadata.UtcStartTime;

            /// <summary>Start instant converted to local time for display.</summary>
            public DateTime LocalStartTime => Metadata.UtcStartTime.ToLocalTime();

            /// <summary>Short local-time display (<c>ToString("g")</c>, current culture).</summary>
            public string LocalStartDisplay => LocalStartTime.ToString("g");

            /// <summary>Recording direction (RX/TX).</summary>
            public string Direction => Metadata.Direction.ToString();

            /// <summary>Protocol string; null renders as empty.</summary>
            public string Protocol => Metadata.Protocol ?? string.Empty;

            /// <summary>System name as configured in the codeplug.</summary>
            public string SystemName => Metadata.SystemName;

            /// <summary>Channel name as configured in the codeplug.</summary>
            public string ChannelName => Metadata.ChannelName;

            /// <summary>Talkgroup id; absent renders as empty.</summary>
            public string TalkgroupId => Metadata.TalkgroupId?.ToString() ?? string.Empty;

            /// <summary>Source (subscriber) id; absent renders as empty.</summary>
            public string SubscriberId => Metadata.SubscriberId?.ToString() ?? string.Empty;

            /// <summary>Source alias; null renders as empty.</summary>
            public string SubscriberAlias => Metadata.SubscriberAlias ?? string.Empty;

            /// <summary>
            /// Duration as <c>hh:mm:ss</c>; negative durations clamp to zero
            /// (WPF oracle behavior).
            /// </summary>
            public string DurationDisplay =>
                TimeSpan.FromMilliseconds(Math.Max(0, Metadata.DurationMs)).ToString(@"hh\:mm\:ss");

            /// <summary>
            /// Encryption summary: "Clear" when unencrypted; "Encrypted" when
            /// encrypted with a blank algorithm; "{Algorithm} / {KeyId:X4}"
            /// when encrypted with a key id; the bare algorithm otherwise.
            /// </summary>
            public string EncryptionSummary => !Metadata.IsEncrypted
                ? "Clear"
                : string.IsNullOrWhiteSpace(Metadata.EncryptionAlgorithm)
                    ? "Encrypted"
                    : Metadata.EncryptionKeyId.HasValue
                        ? $"{Metadata.EncryptionAlgorithm} / {Metadata.EncryptionKeyId.Value:X4}"
                        : Metadata.EncryptionAlgorithm;
        }

        private readonly List<TarRecordingMetadata> loadedRecordings = new List<TarRecordingMetadata>();

        private string searchText = string.Empty;
        private string selectedDirectionFilter = "All";
        private string selectedProtocolFilter = "All";
        private string selectedEncryptionFilter = "All";
        private string systemFilter = string.Empty;
        private string channelFilter = string.Empty;
        private string talkgroupFilter = string.Empty;
        private string sourceIdFilter = string.Empty;
        private string aliasFilter = string.Empty;
        private DateTime? startDateFilter;
        private DateTime? endDateFilter;

        /// <summary>
        /// Creates the view-model over the headless <see cref="TarRecorder"/>
        /// engine. The recorder is the single source of persisted recordings;
        /// filters start at their WPF defaults (all pass) with no rows loaded.
        /// </summary>
        /// <param name="recorder">TAR recording engine; must not be null.</param>
        public TarViewerViewModel(TarRecorder recorder)
        {
            Recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        }

        /// <summary>The headless recording engine backing this view-model.</summary>
        public TarRecorder Recorder { get; }

        /// <summary>Currently visible rows, rebuilt wholesale from the loaded
        /// recordings in recorder order (newest-first) whenever filters change.</summary>
        public ObservableCollection<TarRecordingListItem> Rows { get; } = new ObservableCollection<TarRecordingListItem>();

        /// <summary>Direction filter choices, matching the WPF combo box.</summary>
        public IReadOnlyList<string> DirectionFilters { get; } = new[] { "All", "RX", "TX" };

        /// <summary>Protocol filter choices, matching the WPF combo box.</summary>
        public IReadOnlyList<string> ProtocolFilters { get; } = new[] { "All", "P25", "DMR" };

        /// <summary>Encryption filter choices, matching the WPF combo box.</summary>
        public IReadOnlyList<string> EncryptionFilters { get; } = new[] { "All", "Clear", "Encrypted" };

        /// <summary>Free-text search across system/channel/talkgroup/subscriber/
        /// alias/protocol/file name; empty matches everything.</summary>
        public string SearchText
        {
            get => searchText;
            set => SetFilterProperty(ref searchText, value);
        }

        /// <summary>Selected direction filter ("All" bypasses).</summary>
        public string SelectedDirectionFilter
        {
            get => selectedDirectionFilter;
            set => SetFilterProperty(ref selectedDirectionFilter, value);
        }

        /// <summary>Selected protocol filter ("All" bypasses).</summary>
        public string SelectedProtocolFilter
        {
            get => selectedProtocolFilter;
            set => SetFilterProperty(ref selectedProtocolFilter, value);
        }

        /// <summary>Selected encryption filter ("All" bypasses).</summary>
        public string SelectedEncryptionFilter
        {
            get => selectedEncryptionFilter;
            set => SetFilterProperty(ref selectedEncryptionFilter, value);
        }

        /// <summary>Case-insensitive substring filter over the system name.</summary>
        public string SystemFilter
        {
            get => systemFilter;
            set => SetFilterProperty(ref systemFilter, value);
        }

        /// <summary>Case-insensitive substring filter over the channel name.</summary>
        public string ChannelFilter
        {
            get => channelFilter;
            set => SetFilterProperty(ref channelFilter, value);
        }

        /// <summary>Case-insensitive substring filter over the talkgroup id.</summary>
        public string TalkgroupFilter
        {
            get => talkgroupFilter;
            set => SetFilterProperty(ref talkgroupFilter, value);
        }

        /// <summary>Case-insensitive substring filter over the source id.</summary>
        public string SourceIdFilter
        {
            get => sourceIdFilter;
            set => SetFilterProperty(ref sourceIdFilter, value);
        }

        /// <summary>Case-insensitive substring filter over the subscriber alias.</summary>
        public string AliasFilter
        {
            get => aliasFilter;
            set => SetFilterProperty(ref aliasFilter, value);
        }

        /// <summary>Inclusive local-date start bound; null disables.</summary>
        public DateTime? StartDateFilter
        {
            get => startDateFilter;
            set => SetFilterProperty(ref startDateFilter, value);
        }

        /// <summary>Inclusive local-date end bound; null disables.</summary>
        public DateTime? EndDateFilter
        {
            get => endDateFilter;
            set => SetFilterProperty(ref endDateFilter, value);
        }

        /// <summary>
        /// Reloads persisted recordings from the recorder (optionally rebuilding
        /// the recording index) and rebuilds <see cref="Rows"/> from the returned
        /// newest-first metadata, keeping the current filter set applied.
        /// </summary>
        /// <param name="rebuildIndex">When true, sidecars are re-scanned and the
        /// recorder's index cache rewritten; when false, the cached index is
        /// authoritative (WPF Refresh vs. load behavior).</param>
        public void Refresh(bool rebuildIndex = false)
        {
            loadedRecordings.Clear();
            foreach (TarRecordingMetadata recording in Recorder.LoadRecordings(rebuildIndex))
                loadedRecordings.Add(recording);

            RebuildRows();
        }

        /// <summary>
        /// Resets every filter to its WPF default (All/empty/null) and leaves
        /// <see cref="Rows"/> showing all currently loaded recordings.
        /// </summary>
        public void ClearFilters()
        {
            SearchText = string.Empty;
            SelectedDirectionFilter = "All";
            SelectedProtocolFilter = "All";
            SelectedEncryptionFilter = "All";
            SystemFilter = string.Empty;
            ChannelFilter = string.Empty;
            TalkgroupFilter = string.Empty;
            SourceIdFilter = string.Empty;
            AliasFilter = string.Empty;
            StartDateFilter = null;
            EndDateFilter = null;
        }

        /// <summary>
        /// Change-only filter setter: raises <see cref="PropertyChanged"/> and
        /// immediately rebuilds the filtered <see cref="Rows"/> when the value
        /// actually changed (WPF <c>SetFilterProperty</c> oracle).
        /// </summary>
        private void SetFilterProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return;

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            RebuildRows();
        }

        /// <summary>
        /// Rebuilds <see cref="Rows"/> wholesale from the loaded recordings in
        /// recorder order (Core already sorts newest-first), keeping only the
        /// rows passing every active filter. Mirrors the WPF
        /// <c>ICollectionView</c> re-filter without re-querying the recorder.
        /// </summary>
        private void RebuildRows()
        {
            Rows.Clear();
            foreach (TarRecordingMetadata recording in loadedRecordings)
            {
                TarRecordingListItem item = new TarRecordingListItem { Metadata = recording };
                if (MatchesFilters(item))
                    Rows.Add(item);
            }
        }

        /// <summary>
        /// WPF <c>TarViewerWindow.FilterRecording</c> oracle: free-text search
        /// over system/channel/talkgroup/subscriber/alias/protocol/file name,
        /// independent substring field filters, exact direction/protocol filters
        /// with "All" bypass, Clear/Encrypted encryption filters, and inclusive
        /// local-date start/end bounds.
        /// </summary>
        private bool MatchesFilters(TarRecordingListItem item)
        {
            if (item?.Metadata == null)
                return false;

            if (!MatchesTextFilter(SearchText,
                    item.SystemName,
                    item.ChannelName,
                    item.TalkgroupId,
                    item.SubscriberId,
                    item.SubscriberAlias,
                    item.Protocol,
                    item.Metadata.FileName))
                return false;

            if (!MatchesTextFilter(SystemFilter, item.SystemName))
                return false;
            if (!MatchesTextFilter(ChannelFilter, item.ChannelName))
                return false;
            if (!MatchesTextFilter(TalkgroupFilter, item.TalkgroupId))
                return false;
            if (!MatchesTextFilter(SourceIdFilter, item.SubscriberId))
                return false;
            if (!MatchesTextFilter(AliasFilter, item.SubscriberAlias))
                return false;

            if (!string.Equals(SelectedDirectionFilter, "All", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(item.Direction, SelectedDirectionFilter, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.Equals(SelectedProtocolFilter, "All", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(item.Protocol, SelectedProtocolFilter, StringComparison.OrdinalIgnoreCase))
                return false;

            if (string.Equals(SelectedEncryptionFilter, "Clear", StringComparison.OrdinalIgnoreCase) && item.Metadata.IsEncrypted)
                return false;
            if (string.Equals(SelectedEncryptionFilter, "Encrypted", StringComparison.OrdinalIgnoreCase) && !item.Metadata.IsEncrypted)
                return false;

            DateTime localDate = item.LocalStartTime.Date;
            if (StartDateFilter.HasValue && localDate < StartDateFilter.Value.Date)
                return false;
            if (EndDateFilter.HasValue && localDate > EndDateFilter.Value.Date)
                return false;

            return true;
        }

        /// <summary>
        /// WPF <c>MatchesTextFilter</c> oracle: a blank/whitespace filter passes;
        /// otherwise the trimmed filter must appear (case-insensitive) in at
        /// least one non-blank value.
        /// </summary>
        private static bool MatchesTextFilter(string filter, params string[] values)
        {
            if (string.IsNullOrWhiteSpace(filter))
                return true;

            string trimmedFilter = filter.Trim();
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value) &&
                    value.IndexOf(trimmedFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        /// <inheritdoc cref="INotifyPropertyChanged.PropertyChanged"/>
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
