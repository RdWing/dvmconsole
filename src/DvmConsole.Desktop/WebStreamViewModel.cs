using DvmConsole.Core.Configuration;
using System.ComponentModel;
using System.Windows.Input;

namespace DvmConsole.Desktop;

public sealed class WebStreamViewModel : INotifyPropertyChanged
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

    public WebStreamViewModel(WebStreamConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Name = configuration.Name.Trim();
        Url = configuration.Url.Trim();
        AuthUsername = configuration.AuthUsername?.Trim() ?? string.Empty;
        AuthPassword = configuration.AuthPassword ?? string.Empty;
        ToggleCommand = new AsyncRelayCommand(() => Task.CompletedTask, () => false);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<double>? VolumeChanged;

    public string Name { get; }
    public string Url { get; }
    public string AuthUsername { get; }
    public string AuthPassword { get; }
    public bool IsActive => isActive;
    public bool IsConnecting => isConnecting;
    public bool IsReceiving => isReceiving;
    public bool IsFailed => isFailed;
    public string StatusText => statusText;
    public string ToggleButtonText => IsActive ? "Stop" : "Start";
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
            VolumeChanged?.Invoke(this, normalized);
        }
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
        (ToggleCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private async Task ToggleAsync()
    {
        if (start is null || stop is null)
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
               outputDeviceOptions.FirstOrDefault();
    }
}
