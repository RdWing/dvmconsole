using Avalonia.Controls;
using Avalonia.Threading;
using DvmConsole.Core.Configuration;
using DvmConsole.FneClient;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace DvmConsole.Desktop;

public sealed partial class MainWindow : Window
{
    public MainWindow() : this(null)
    {
    }

    public MainWindow(string? configurationPath)
    {
        InitializeComponent();
        var viewModel = MainWindowViewModel.Load(configurationPath);
        DataContext = viewModel;
        Closed += async (_, _) => await viewModel.DisposeAsync().ConfigureAwait(false);
    }

    private void InitializeComponent()
    {
        Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
    }
}

public sealed class MainWindowViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private string statusText;
    private bool busy;

    private MainWindowViewModel(string statusText, IEnumerable<SystemViewModel> systems, IEnumerable<ZoneViewModel> zones)
    {
        this.statusText = statusText;
        Systems = systems.ToArray();
        Zones = zones.ToArray();
        foreach (SystemViewModel system in Systems)
            system.StatusChanged += (_, status) => HandleSystemStatus(system, status);

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !busy && Systems.Count > 0);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => !busy && Systems.Count > 0);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string StatusText
    {
        get => statusText;
        private set => SetField(ref statusText, value);
    }

    public IReadOnlyList<SystemViewModel> Systems { get; }
    public IReadOnlyList<ZoneViewModel> Zones { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }

    public static MainWindowViewModel Load(string? configurationPath)
    {
        if (string.IsNullOrWhiteSpace(configurationPath))
        {
            return new MainWindowViewModel(
                "No codeplug selected. Launch with a path to a codeplug YAML file.",
                [],
                []);
        }

        try
        {
            ConsoleConfiguration configuration = ConfigurationLoader.Load(configurationPath);
            IReadOnlyList<string> errors = ConfigurationLoader.Validate(configuration);
            string status = errors.Count == 0
                ? $"Loaded {configuration.Systems.Count} system(s) and {configuration.Zones.Count} zone(s). Connections are idle until Connect is pressed."
                : $"Configuration has {errors.Count} validation error(s).";

            return new MainWindowViewModel(
                status,
                errors.Count == 0
                    ? configuration.Systems.Select(system => new SystemViewModel(
                        FneConnectionOptions.FromConfiguration(system),
                        system.Name,
                        $"{system.Address}:{system.Port}"))
                    : [],
                configuration.Zones.Select(zone => new ZoneViewModel(
                    zone.Name,
                    zone.Channels.Select(channel => new ChannelViewModel(
                        channel.Name,
                        channel.Mode.ToUpperInvariant(),
                        $"{channel.System} / TGID {channel.Tgid}")).ToArray())));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or YamlDotNet.Core.YamlException)
        {
            return new MainWindowViewModel($"Unable to load codeplug: {exception.Message}", [], []);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (SystemViewModel system in Systems)
            await system.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ConnectAsync()
    {
        SetBusy(true);
        StatusText = "Starting FNE connection services...";
        try
        {
            await Task.WhenAll(Systems.Select(system => StartSystemAsync(system)));
            StatusText = "FNE connection services started; waiting for login acknowledgements.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task StartSystemAsync(SystemViewModel system)
    {
        try
        {
            await system.StartAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            HandleSystemStatus(system, new FneConnectionStatus(
                system.Name,
                FneConnectionState.Faulted,
                exception.Message,
                DateTimeOffset.UtcNow));
        }
    }

    private async Task DisconnectAsync()
    {
        SetBusy(true);
        StatusText = "Stopping FNE connection services...";
        try
        {
            await Task.WhenAll(Systems.Select(system => system.StopAsync()));
            StatusText = "FNE connections stopped.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void HandleSystemStatus(SystemViewModel system, FneConnectionStatus status)
    {
        void Apply()
        {
            system.ApplyStatus(status);
            StatusText = $"{system.Name}: {status.State} — {status.Message}";
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
    }

    private void SetBusy(bool value)
    {
        busy = value;
        (ConnectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DisconnectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class SystemViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly FneConnection connection;
    private string connectionStatus = "Disconnected";

    public SystemViewModel(FneConnectionOptions options, string name, string endpoint)
    {
        connection = new FneConnection(options);
        Name = name;
        Endpoint = endpoint;
        connection.StatusChanged += HandleConnectionStatus;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<FneConnectionStatus>? StatusChanged;
    public string Name { get; }
    public string Endpoint { get; }
    public string ConnectionStatus
    {
        get => connectionStatus;
        private set
        {
            if (connectionStatus == value)
                return;
            connectionStatus = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectionStatus)));
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => connection.StartAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken = default) => connection.StopAsync(cancellationToken);

    public void ApplyStatus(FneConnectionStatus status)
    {
        ConnectionStatus = $"{status.State}: {status.Message}";
    }

    public async ValueTask DisposeAsync()
    {
        connection.StatusChanged -= HandleConnectionStatus;
        await connection.DisposeAsync().ConfigureAwait(false);
    }

    private void HandleConnectionStatus(object? sender, FneConnectionStatus status)
    {
        StatusChanged?.Invoke(this, status);
    }
}

public sealed record ZoneViewModel(string Name, IReadOnlyList<ChannelViewModel> Channels);

public sealed record ChannelViewModel(string Name, string ModeText, string DestinationText);

internal sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> execute;
    private readonly Func<bool> canExecute;
    private bool running;

    public AsyncRelayCommand(Func<Task> execute, Func<bool> canExecute)
    {
        this.execute = execute;
        this.canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !running && canExecute();

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
            return;
        running = true;
        RaiseCanExecuteChanged();
        try
        {
            await execute();
        }
        finally
        {
            running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
