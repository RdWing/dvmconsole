using Avalonia.Threading;
using DvmConsole.Operations;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DvmConsole.Desktop;

internal sealed class EngineeringHealthViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly DispatcherTimer refreshTimer;
    private MainWindowViewModel console;
    private RuntimeHealthSnapshot? runtimeHealth;
    private int refreshRunning;
    private bool active;
    private bool disposed;

    public EngineeringHealthViewModel(MainWindowViewModel console)
    {
        this.console = console ?? throw new ArgumentNullException(nameof(console));
        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        refreshTimer.Tick += HandleRefreshTimerTick;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string CapturedAtText
        => runtimeHealth is null
            ? "Waiting for a health sample"
            : $"Updated {runtimeHealth.CapturedAt.ToLocalTime():HH:mm:ss}";

    public string ReceiveQueueHealthText
        => runtimeHealth is null
            ? "RX queue: checking"
            : OperationalHealthPresentation.FormatReceiveQueue(runtimeHealth.ReceiveQueue);

    public string ReceiveLatencyHealthText
        => runtimeHealth is null
            ? "RX latency: checking"
            : OperationalHealthPresentation.FormatLatency(runtimeHealth.ReceiveLatency);

    public string MicrophoneHealthText
        => runtimeHealth is null
            ? "Microphone: checking"
            : OperationalHealthPresentation.FormatMicrophoneEngineering(runtimeHealth.Microphone);

    public string TransmitBacklogHealthText
        => runtimeHealth is null
            ? "TX work: checking"
            : OperationalHealthPresentation.FormatWorkBacklog("TX work", runtimeHealth.Transmit);

    public string FinalizationHealthText
        => runtimeHealth is null
            ? "TAR finalization: checking"
            : OperationalHealthPresentation.FormatWorkBacklog(
                "TAR finalization",
                runtimeHealth.RecordingFinalization);

    public string CatalogHealthText
        => runtimeHealth is null
            ? "TAR catalog: not scanned"
            : OperationalHealthPresentation.FormatCatalog(runtimeHealth.RecordingCatalog);

    public string RouteRecoveryHealthText
        => runtimeHealth is null
            ? "Route recovery: checking"
            : OperationalHealthPresentation.FormatRouteRecovery(runtimeHealth);

    public string ConnectionHealthText
        => console.SelectedSystem?.ConnectionHealthText ?? "Connection: no system selected";

    public void ReplaceConsole(MainWindowViewModel replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (ReferenceEquals(console, replacement))
            return;
        console = replacement;
        runtimeHealth = null;
        RaiseAll();
        if (active)
            QueueRefresh();
    }

    public void SetActive(bool value)
    {
        if (disposed || active == value)
            return;
        active = value;
        if (active)
        {
            refreshTimer.Start();
            QueueRefresh();
        }
        else
        {
            refreshTimer.Stop();
        }
    }

    private void HandleRefreshTimerTick(object? sender, EventArgs e)
        => QueueRefresh();

    private void QueueRefresh()
    {
        if (!active || disposed || Interlocked.Exchange(ref refreshRunning, 1) != 0)
            return;
        MainWindowViewModel owner = console;
        TaskObservation.Observe(RefreshAsync(owner));
    }

    private async Task RefreshAsync(MainWindowViewModel owner)
    {
        try
        {
            RuntimeHealthSnapshot snapshot = await Task.Run(owner.CaptureRuntimeHealthSnapshot)
                .ConfigureAwait(true);
            if (disposed || !active || !ReferenceEquals(owner, console))
                return;
            runtimeHealth = snapshot;
            RaiseAll();
        }
        catch (ObjectDisposedException) when (disposed || !ReferenceEquals(owner, console))
        {
        }
        finally
        {
            Interlocked.Exchange(ref refreshRunning, 0);
        }
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(CapturedAtText));
        OnPropertyChanged(nameof(ReceiveQueueHealthText));
        OnPropertyChanged(nameof(ReceiveLatencyHealthText));
        OnPropertyChanged(nameof(MicrophoneHealthText));
        OnPropertyChanged(nameof(TransmitBacklogHealthText));
        OnPropertyChanged(nameof(FinalizationHealthText));
        OnPropertyChanged(nameof(CatalogHealthText));
        OnPropertyChanged(nameof(RouteRecoveryHealthText));
        OnPropertyChanged(nameof(ConnectionHealthText));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public ValueTask DisposeAsync()
    {
        if (disposed)
            return ValueTask.CompletedTask;
        disposed = true;
        active = false;
        refreshTimer.Stop();
        refreshTimer.Tick -= HandleRefreshTimerTick;
        return ValueTask.CompletedTask;
    }
}
