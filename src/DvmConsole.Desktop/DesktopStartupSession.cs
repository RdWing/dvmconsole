// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Configuration.Yaml;
using DvmConsole.Core.Settings;

namespace DvmConsole.Desktop;

/// <summary>Owns prepared startup resources until a window accepts the session.</summary>
internal sealed class DesktopStartupSession : IAsyncDisposable
{
    private readonly AsyncDisposal disposal = new();
    private bool transferred;
    private MainWindowViewModel? viewModel;
    private readonly UserSettingsStore settingsStore;
    private readonly bool demoMode;
    private string? preparedPath;
    private bool migrateOperatorState;
    public bool IsInitialized => viewModel is not null;
    public MainWindowViewModel ViewModel => viewModel!;
    public DesktopConfigurationStoreContext Stores { get; }
    public ShutdownTimingRecorder ShutdownTiming { get; }
    public ConfigurationReference? ActiveConfiguration { get; private set; }
    public IConfigurationMaterializationLease? Materialization { get; private set; }
    public ConfigurationReference? PendingActiveConfiguration { get; private set; }
    public string? PendingImportPath { get; private set; }

    private DesktopStartupSession(DesktopConfigurationStoreContext stores, string root, UserSettingsStore settingsStore, bool demoMode)
    {
        Stores = stores;
        ShutdownTiming = new(root);
        this.settingsStore = settingsStore;
        this.demoMode = demoMode;
    }

    public static async Task<DesktopStartupSession> PrepareAsync(string? requestedPath,
        UserSettingsStore settingsStore, bool demoMode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        cancellationToken.ThrowIfCancellationRequested();
        string root = Path.GetDirectoryName(settingsStore.Path) ?? AppContext.BaseDirectory;
        var stores = await DesktopConfigurationStoreContext.CreateAsync(root,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var startup = new DesktopStartupSession(stores, root, settingsStore, demoMode);
        try
        {
            await startup.PrepareConfigurationAsync(requestedPath, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return startup;
        }
        catch (Exception preparationFailure)
        {
            await ConsoleSessionConstruction.RollbackAsync(preparationFailure, startup.DisposeAsync).ConfigureAwait(false);
            throw;
        }
    }

    private async Task PrepareConfigurationAsync(string? requestedPath, CancellationToken token)
    {
        UserSettings settings = settingsStore.Load();
        bool persistedStartup = LegacyOperatorStateAttributionPolicy.ShouldAttributeToOpenedConfiguration(
            requestedPath, settings.LastCodeplugPath);
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            if (Stores.Library.Active is { } active) PendingActiveConfiguration = active;
            else requestedPath = settings.LastCodeplugPath;
        }
        bool migrate = false;
        if (!string.IsNullOrWhiteSpace(requestedPath))
        {
            string legacyPath = Path.GetFullPath(requestedPath);
            requestedPath = legacyPath;
            if (demoMode)
            {
                try
                {
                    var imported = await Stores.Library.ImportAsync(new FileConfigurationDocumentSet(legacyPath),
                        new ConfigurationImportOptions(), token).ConfigureAwait(false);
                    await Stores.Library.ActivateAsync(imported.Reference, token).ConfigureAwait(false);
                    Materialization = await Stores.Materializer.MaterializeAsync(imported.Reference, token).ConfigureAwait(false);
                    MigrateOperatorState(settingsStore, legacyPath, Materialization.Path);
                    requestedPath = Materialization.Path;
                    ActiveConfiguration = imported.Reference;
                    migrate = persistedStartup;
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or
                    UnauthorizedAccessException or ConfigurationImportConflictException or
                    ConfigurationExternalCompanionsConfirmationRequiredException)
                {
                    if (Materialization is not null)
                    {
                        await Materialization.DisposeAsync().ConfigureAwait(false);
                        Materialization = null;
                    }
                    DesktopCrashLog.Write("Demo configuration library import", exception);
                }
            }
            else PendingImportPath = legacyPath;
        }
        preparedPath = requestedPath;
        migrateOperatorState = migrate;
    }

    // Desktop view models contain Avalonia presentation objects. The host calls
    // this phase on its UI dispatcher after storage preparation has completed.
    public async Task InitializeViewModelAsync(CancellationToken token = default)
    {
        if (IsInitialized) throw new InvalidOperationException("Startup presentation is already initialized.");
        viewModel = await MainWindowViewModel.LoadAsync(preparedPath, settingsStore,
            networkDisabledDemo: demoMode, configurationReference: ActiveConfiguration,
            useLegacyPathFallback: false, migrateLegacyConfigurationOperatorState: migrateOperatorState,
            serviceDisposalObserved: ShutdownTiming.ObserveService, cancellationToken: token);
        if (demoMode) viewModel.InitializeDemoScenario();
    }

    internal static void MigrateOperatorState(UserSettingsStore store, string legacyPath, string managedPath)
    {
        if (FileSystemPathIdentity.AreEquivalent(legacyPath, managedPath)) return;
        UserSettings settings = store.Load();
        _ = CodeplugGroupStateStore.CopyForSaveAs(settings, legacyPath, managedPath);
        _ = CodeplugStudioStateStore.CopyForSaveAs(settings, legacyPath, managedPath);
        store.Save(settings);
    }

    public void TransferOwnership() => transferred = true;
    public ValueTask DisposeAsync() => disposal.RunAsync(async () =>
    {
        if (transferred) return;
        var cleanup = new AsyncCleanup();
        if (viewModel is not null) await cleanup.RunTaskAsync(() => viewModel.DisposeAsync().AsTask()).ConfigureAwait(false);
        if (Materialization is not null) await cleanup.RunTaskAsync(() => Materialization.DisposeAsync().AsTask()).ConfigureAwait(false);
        cleanup.ThrowIfFailed();
    });
}
