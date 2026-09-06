// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using DvmConsole.Presentation;
using Avalonia.Media;
using System.ComponentModel;
using System.Windows.Input;

namespace DvmConsole.Desktop;

public sealed class WebStreamViewModel : INotifyPropertyChanged, IWebStreamViewModel
{
    private Func<WebStreamViewModel, Task>? start;
    private Func<WebStreamViewModel, Task>? stop;
    private bool busy;
    private bool isActive;
    private bool isConnecting;
    private bool isReceiving;
    private bool isFailed;
    private double volume = 1.0;
    private string outputDeviceIdText = string.Empty;
    private string statusText = "Off";
    private IReadOnlyList<AudioDeviceOptionViewModel> outputDeviceOptions = [];
    private readonly string? idleColor;
    private double widgetX;
    private double widgetY;
    private bool darkMode;

    public WebStreamViewModel(WebStreamConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Name = configuration.Name.Trim();
        Url = configuration.Url.Trim();
        AuthUsername = configuration.AuthUsername?.Trim() ?? string.Empty;
        AuthPassword = configuration.AuthPassword ?? string.Empty;
        idleColor = configuration.IdleColor;
        ToggleCommand = new AsyncRelayCommand(() => Task.CompletedTask, () => false);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<double>? VolumeChanged;
    public event EventHandler<WidgetPositionChangedEventArgs>? WidgetPositionChanged;

    public string Name { get; }
    public string SettingsKey => WidgetPositionKey.ForWebStream(Name);
    public string Url { get; }
    public string AuthUsername { get; }
    public string AuthPassword { get; }
    public bool IsActive => isActive;
    public bool IsConnecting => isConnecting;
    public bool IsReceiving => isReceiving;
    public bool IsFailed => isFailed;
    public string StatusText => statusText;
    public string ToggleButtonText => IsActive ? "Stop" : "Start";
    public string CardSubtitle => "Web stream · RX only";
    public string CardStatusText => IsReceiving ? "Live audio" : StatusText;
    public string ToggleAutomationName => $"{ToggleButtonText} web stream {Name}";
    public string VolumeAutomationName => $"Volume for web stream {Name}";
    public double CardWidth => 235;
    public double WidgetX => widgetX;
    public double WidgetY => widgetY;
    public IBrush CardBackgroundBrush => IsReceiving
        ? SolidBrushCache.Get("#008A3A")
        : IsActive
            ? SolidBrushCache.Get(darkMode ? "#1B2B22" : "#E2F3E8")
            : SolidBrushCache.Get(darkMode ? "#151D26" : "#FFFFFF");
    public IBrush CardBorderBrush => IsFailed
        ? SolidBrushCache.Get("#C73A3A")
        : IsReceiving
            ? SolidBrushCache.Get("#00C86A")
            : IsConnecting
                ? SolidBrushCache.Get("#D99920")
                : IsActive
                    ? SolidBrushCache.Get("#4E8060")
                    : SolidBrushCache.Get(
                        idleColor ?? string.Empty,
                        darkMode ? "#2A3A4B" : "#9BA8B5");
    public IBrush CardTextBrush => IsReceiving
        ? SolidBrushCache.Get("#FFFFFF")
        : SolidBrushCache.Get(darkMode ? "#DCE3EB" : "#18212B");
    public string OutputDeviceIdText
    {
        get => outputDeviceIdText;
        set
        {
            string normalized = value?.Trim() ?? string.Empty;
            if (outputDeviceIdText.Equals(normalized, StringComparison.Ordinal))
                return;
            outputDeviceIdText = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OutputDeviceIdText)));
        }
    }
    public IReadOnlyList<AudioDeviceOptionViewModel> OutputDeviceOptions => outputDeviceOptions;
    public AudioDeviceOptionViewModel? SelectedOutputDevice
    {
        get => ResolveOutputDevice();
        set
        {
            if (value is not null)
                OutputDeviceIdText = value.Id;
        }
    }
    System.Collections.IEnumerable IWebStreamViewModel.OutputDeviceOptions => OutputDeviceOptions;
    IAudioDeviceOptionViewModel? IWebStreamViewModel.SelectedOutputDevice
    {
        get => SelectedOutputDevice;
        set => SelectedOutputDevice = value as AudioDeviceOptionViewModel;
    }
    public double Volume
    {
        get => volume;
        set
        {
            double normalized = double.IsFinite(value) ? Math.Clamp(value, 0, 4) : 1.0;
            if (Math.Abs(volume - normalized) < 0.0001)
                return;
            volume = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Volume)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VolumeSliderValue)));
            VolumeChanged?.Invoke(this, normalized);
        }
    }

    public double VolumeSliderValue
    {
        get => NeutralSliderMath.VolumeGainToPosition(volume);
        set => Volume = NeutralSliderMath.VolumePositionToGain(value);
    }

    public ICommand ToggleCommand { get; private set; }

    public void Configure(
        Func<WebStreamViewModel, Task> start,
        Func<WebStreamViewModel, Task> stop)
    {
        this.start = start ?? throw new ArgumentNullException(nameof(start));
        this.stop = stop ?? throw new ArgumentNullException(nameof(stop));
        ToggleCommand = new AsyncRelayCommand(ToggleAsync, () => !busy);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToggleCommand)));
    }

    public void SetInitialVolume(double value)
        => Volume = value;

    public void RestoreOutputDeviceId(string? value)
        => OutputDeviceIdText = value ?? string.Empty;

    public void SetOutputDeviceOptions(IReadOnlyList<AudioDeviceOptionViewModel> options)
    {
        outputDeviceOptions = options ?? [];
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OutputDeviceOptions)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedOutputDevice)));
    }

    public void RefreshOutputDeviceSelection()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedOutputDevice)));

    public void SetWidgetPosition(double x, double y, bool isFinal = false)
    {
        double nextX = double.IsFinite(x) ? Math.Clamp(x, 0, 10_000) : 0;
        double nextY = double.IsFinite(y) ? Math.Clamp(y, 0, 10_000) : 0;
        bool changed = false;
        if (Math.Abs(widgetX - nextX) >= 0.01)
        {
            widgetX = nextX;
            changed = true;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WidgetX)));
        }
        if (Math.Abs(widgetY - nextY) >= 0.01)
        {
            widgetY = nextY;
            changed = true;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WidgetY)));
        }
        if (changed || isFinal)
        {
            WidgetPositionChanged?.Invoke(
                this,
                new WidgetPositionChangedEventArgs(widgetX, widgetY, isFinal));
        }
    }

    public void SetDarkMode(bool enabled)
    {
        if (darkMode == enabled)
            return;
        darkMode = enabled;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardBackgroundBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardBorderBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardTextBrush)));
    }

    internal void SetPlaybackState(
        bool active,
        bool connecting,
        bool receiving,
        bool failed,
        string status)
    {
        isActive = active;
        isConnecting = connecting;
        isReceiving = receiving;
        isFailed = failed;
        statusText = status;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsConnecting)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReceiving)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFailed)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToggleButtonText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardStatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToggleAutomationName)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardBackgroundBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardBorderBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardTextBrush)));
        (ToggleCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    internal async Task ToggleAsync()
    {
        if (busy || start is null || stop is null)
            return;

        busy = true;
        (ToggleCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        try
        {
            if (IsActive)
                await stop(this);
            else
                await start(this);
        }
        finally
        {
            busy = false;
            (ToggleCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private AudioDeviceOptionViewModel? ResolveOutputDevice()
    {
        return outputDeviceOptions.FirstOrDefault(device =>
                   !string.IsNullOrWhiteSpace(OutputDeviceIdText) &&
                   device.Id.Equals(OutputDeviceIdText, StringComparison.OrdinalIgnoreCase)) ??
               outputDeviceOptions.FirstOrDefault(device => device.IsDefault) ??
               (outputDeviceOptions.Count > 0 ? outputDeviceOptions[0] : null);
    }
}
