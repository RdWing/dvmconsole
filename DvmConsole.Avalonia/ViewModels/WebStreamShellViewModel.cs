// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using dvmconsole;
using DvmConsole.Avalonia.Services;
using DvmConsole.Platform.Audio;

namespace DvmConsole.Avalonia.ViewModels
{
    /// <summary>
    /// Shell-owned collection of configured web-stream playback items. The
    /// collection owns item lifetimes but borrows the shared source and audio
    /// factories from the application shell.
    /// </summary>
    public sealed class WebStreamShellViewModel : IAsyncDisposable
    {
        private readonly Action<Action> dispatch;
        private readonly Func<AudioDeviceId> outputDevice;
        private readonly IWebStreamSourceFactory? sourceFactory;
        private readonly IAudioStreamFactory? audioFactory;

        public WebStreamShellViewModel(
            IEnumerable<WebStreamShellDefinition>? definitions,
            bool restoreSelected,
            IEnumerable<string>? selectedNames,
            IReadOnlyDictionary<string, double>? volumes,
            IReadOnlyDictionary<string, UserSettingsLayoutPosition>? positions,
            IWebStreamSourceFactory? sourceFactory,
            IAudioStreamFactory? audioFactory,
            Func<AudioDeviceId> outputDevice,
            Action<Action>? dispatch = null)
        {
            this.sourceFactory = sourceFactory;
            this.audioFactory = audioFactory;
            this.outputDevice = outputDevice ?? throw new ArgumentNullException(nameof(outputDevice));
            this.dispatch = dispatch ?? (action => action());

            var projection = new WebStreamShellProjection(
                definitions,
                restoreSelected,
                selectedNames,
                volumes,
                positions);
            Items = new ObservableCollection<WebStreamShellItemViewModel>(
                projection.Items.Select(item => new WebStreamShellItemViewModel(
                    item,
                    sourceFactory,
                    audioFactory,
                    outputDevice,
                    this.dispatch)));
        }

        public ObservableCollection<WebStreamShellItemViewModel> Items { get; }

        public bool CanPlay => sourceFactory is not null && audioFactory is not null;

        public async Task StartRestoredAsync()
        {
            foreach (var item in Items.Where(item => item.ShouldRestoreActive))
            {
                await item.StartAsync().ConfigureAwait(false);
            }
        }

        public WebStreamShellSettingsSnapshot Snapshot()
        {
            var selected = Items
                .Where(item => item.IsActive)
                .Select(item => item.DisplayName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var volumes = Items
                .GroupBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last().Volume, StringComparer.OrdinalIgnoreCase);
            var positions = Items
                .GroupBy(item => item.PositionKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group =>
                    new UserSettingsLayoutPosition
                    {
                        X = group.Last().Position.X,
                        Y = group.Last().Position.Y,
                    }, StringComparer.OrdinalIgnoreCase);

            return new WebStreamShellSettingsSnapshot(selected, volumes, positions);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var item in Items)
            {
                await item.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public sealed record WebStreamShellSettingsSnapshot(
        IReadOnlyList<string> SelectedNames,
        IReadOnlyDictionary<string, double> Volumes,
        IReadOnlyDictionary<string, UserSettingsLayoutPosition> Positions);

    /// <summary>
    /// One bindable web-stream chip state. Coordinator creation is lazy so a
    /// host without an audio factory can still render configured streams as
    /// unavailable without touching native audio.
    /// </summary>
    public sealed class WebStreamShellItemViewModel : INotifyPropertyChanged, IAsyncDisposable
    {
        private readonly IWebStreamSourceFactory? sourceFactory;
        private readonly IAudioStreamFactory? audioFactory;
        private readonly Func<AudioDeviceId> outputDevice;
        private readonly Action<Action> dispatch;
        private readonly SemaphoreSlim transition = new(1, 1);
        private WebStreamPlaybackCoordinator? coordinator;
        private WebStreamShellPosition position;
        private double volume;
        private string statusText = "Off";
        private bool isActive;
        private bool isReceiving;
        private int disposed;

        public WebStreamShellItemViewModel(
            WebStreamShellItem item,
            IWebStreamSourceFactory? sourceFactory,
            IAudioStreamFactory? audioFactory,
            Func<AudioDeviceId> outputDevice,
            Action<Action> dispatch)
        {
            Definition = item.Definition;
            ZoneName = item.ZoneName;
            DisplayName = item.DisplayName;
            StreamUrl = item.Definition.Url?.Trim() ?? string.Empty;
            ShouldRestoreActive = item.ShouldRestoreActive;
            position = item.Position;
            volume = item.Volume;
            this.sourceFactory = sourceFactory;
            this.audioFactory = audioFactory;
            this.outputDevice = outputDevice;
            this.dispatch = dispatch;
        }

        public Codeplug.WebStream Definition { get; }
        public string ZoneName { get; }
        public string DisplayName { get; }
        public string StreamUrl { get; }
        public bool ShouldRestoreActive { get; }
        public bool CanPlay => sourceFactory is not null && audioFactory is not null;
        public string PositionKey => WebStreamShellProjection.BuildPositionKey(ZoneName, DisplayName);
        public string ToggleButtonText => IsActive ? "STOP" : "START";

        public WebStreamShellPosition Position
        {
            get => position;
            private set
            {
                if (position == value)
                    return;
                position = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Position)));
            }
        }

        public double Volume
        {
            get => volume;
            set
            {
                var normalized = NormalizeVolume(value);
                if (Math.Abs(volume - normalized) < 0.0001)
                    return;
                volume = normalized;
                if (coordinator is not null)
                    coordinator.Volume = normalized;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Volume)));
            }
        }

        public string StatusText
        {
            get => statusText;
            private set
            {
                if (statusText == value)
                    return;
                statusText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
            }
        }

        public bool IsActive
        {
            get => isActive;
            private set
            {
                if (isActive == value)
                    return;
                isActive = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToggleButtonText)));
            }
        }

        public bool IsReceiving
        {
            get => isReceiving;
            private set
            {
                if (isReceiving == value)
                    return;
                isReceiving = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReceiving)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void SetPosition(double x, double y)
            => Position = new WebStreamShellPosition(Math.Max(0, x), Math.Max(0, y));

        public async Task StartAsync()
        {
            await transition.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref disposed) != 0 || !CanPlay)
                    return;

                coordinator ??= CreateCoordinator();
                coordinator.Volume = Volume;
                _ = ObserveRunAsync(coordinator.StartAsync());
            }
            finally
            {
                transition.Release();
            }
        }

        public async Task StopAsync()
        {
            await transition.WaitAsync().ConfigureAwait(false);
            try
            {
                if (coordinator is not null)
                    await coordinator.StopAsync().ConfigureAwait(false);
            }
            finally
            {
                transition.Release();
            }
        }

        public async Task ToggleAsync()
        {
            if (IsActive)
                await StopAsync().ConfigureAwait(false);
            else
                await StartAsync().ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            await transition.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref disposed) != 0)
                    return;
                Volatile.Write(ref disposed, 1);
                if (coordinator is not null)
                {
                    coordinator.StateChanged -= OnStateChanged;
                    await coordinator.DisposeAsync().ConfigureAwait(false);
                    coordinator = null;
                }
            }
            finally
            {
                transition.Release();
                transition.Dispose();
            }
        }

        private WebStreamPlaybackCoordinator CreateCoordinator()
        {
            var created = new WebStreamPlaybackCoordinator(
                Definition,
                sourceFactory!,
                audioFactory!,
                outputDevice());
            created.StateChanged += OnStateChanged;
            return created;
        }

        private static async Task ObserveRunAsync(Task run)
        {
            try
            {
                await run.ConfigureAwait(false);
            }
            catch
            {
                // The coordinator publishes terminal state; the shell must
                // not surface an unobserved task fault on the UI thread.
            }
        }

        private void OnStateChanged(WebStreamPlaybackState state)
            => dispatch(() =>
            {
                if (Volatile.Read(ref disposed) != 0)
                    return;

                StatusText = state.StatusText;
                IsActive = state.IsActive;
                IsReceiving = state.IsReceiving;
                if (Math.Abs(volume - state.Volume) >= 0.0001)
                {
                    volume = state.Volume;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Volume)));
                }
            });

        private static double NormalizeVolume(double value)
            => double.IsNaN(value) || double.IsInfinity(value)
                ? 1.0
                : Math.Clamp(Math.Round(value * 10.0) / 10.0, 0.0, 4.0);
    }
}
