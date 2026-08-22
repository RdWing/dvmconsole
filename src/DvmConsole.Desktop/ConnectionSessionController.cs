using DvmConsole.FneClient;

namespace DvmConsole.Desktop;

internal sealed class ConnectionSessionController
{
    private readonly IReadOnlyList<SystemViewModel> systems;
    private readonly Func<Task> synchronizePatchSources;
    private readonly Func<Task> stopPatchSources;
    private readonly Action stopPatchForwarding;
    private readonly Action<bool> setBusy;
    private readonly Action<string> setStatus;
    private readonly Action<SystemViewModel> selectSystem;
    private readonly Action<SystemViewModel, FneConnectionStatus> publishStatus;

    public ConnectionSessionController(
        IReadOnlyList<SystemViewModel> systems,
        Func<Task> synchronizePatchSources,
        Func<Task> stopPatchSources,
        Action stopPatchForwarding,
        Action<bool> setBusy,
        Action<string> setStatus,
        Action<SystemViewModel> selectSystem,
        Action<SystemViewModel, FneConnectionStatus> publishStatus)
    {
        this.systems = systems ?? throw new ArgumentNullException(nameof(systems));
        this.synchronizePatchSources = synchronizePatchSources ?? throw new ArgumentNullException(nameof(synchronizePatchSources));
        this.stopPatchSources = stopPatchSources ?? throw new ArgumentNullException(nameof(stopPatchSources));
        this.stopPatchForwarding = stopPatchForwarding ?? throw new ArgumentNullException(nameof(stopPatchForwarding));
        this.setBusy = setBusy ?? throw new ArgumentNullException(nameof(setBusy));
        this.setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        this.selectSystem = selectSystem ?? throw new ArgumentNullException(nameof(selectSystem));
        this.publishStatus = publishStatus ?? throw new ArgumentNullException(nameof(publishStatus));
    }

    public async Task ConnectAsync()
    {
        setBusy(true);
        setStatus("Starting FNE connection services...");
        try
        {
            await Task.WhenAll(systems.Select(StartSystemAsync));
            await synchronizePatchSources().ConfigureAwait(false);
            setStatus("FNE connection services started; waiting for login acknowledgements.");
        }
        finally
        {
            setBusy(false);
        }
    }

    public async Task DisconnectAsync()
    {
        setBusy(true);
        setStatus("Stopping FNE connection services...");
        try
        {
            await stopPatchSources().ConfigureAwait(false);
            stopPatchForwarding();
            await Task.WhenAll(systems.Select(system => system.StopAsync()));
            setStatus("FNE connections stopped.");
        }
        finally
        {
            setBusy(false);
        }
    }

    public async Task ToggleAsync(SystemViewModel system)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (!systems.Contains(system))
            throw new ArgumentException("The FNE is not part of this console.", nameof(system));

        selectSystem(system);
        if (system.IsConnectionActive)
        {
            setStatus($"Stopping {system.Name}...");
            try
            {
                await system.StopAsync();
                setStatus($"{system.Name}: disconnected.");
            }
            catch (Exception exception)
            {
                setStatus($"{system.Name}: disconnect failed — {exception.Message}");
            }
            return;
        }

        setStatus($"Starting {system.Name}...");
        await StartSystemAsync(system);
        await synchronizePatchSources();
    }

    private async Task StartSystemAsync(SystemViewModel system)
    {
        try
        {
            await system.StartAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            publishStatus(system, new FneConnectionStatus(
                system.Name,
                FneConnectionState.Faulted,
                exception.Message,
                DateTimeOffset.UtcNow));
        }
    }
}
