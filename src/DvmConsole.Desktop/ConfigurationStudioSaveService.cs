using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;

namespace DvmConsole.Desktop;

internal sealed record ConfigurationStudioSaveExecution(
    ConfigurationSavePlan Plan,
    ConfigurationSaveResult? Result,
    Exception? SettingsPersistenceFailure);

/// <summary>
/// Owns the non-visual Configuration Studio save workflow: rebasing against
/// live operator settings, executing the all-or-nothing file transaction, and
/// adopting the saved settings snapshot back into the running console.
/// </summary>
internal sealed class ConfigurationStudioSaveService
{
    private readonly ConfigurationStudioViewModel studio;
    private readonly MainWindowViewModel runtime;
    private readonly UserSettingsStore settingsStore;

    public ConfigurationStudioSaveService(
        ConfigurationStudioViewModel studio,
        MainWindowViewModel runtime,
        UserSettingsStore settingsStore)
    {
        this.studio = studio ?? throw new ArgumentNullException(nameof(studio));
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
    }

    public async Task<ConfigurationSavePlan> PrepareAsync(string path)
    {
        await runtime.FlushUserSettingsAsync().ConfigureAwait(false);
        return studio.CreateSavePlan(path);
    }

    public async Task<ConfigurationStudioSaveExecution> ExecuteAsync(string path)
    {
        ConfigurationSavePlan plan = await PrepareAsync(path).ConfigureAwait(false);
        if (!plan.CanSave)
            return new ConfigurationStudioSaveExecution(plan, null, null);

        string backupRoot = Path.Combine(
            Path.GetDirectoryName(settingsStore.Path) ?? AppContext.BaseDirectory,
            "ConfigurationBackups");
        ConfigurationSaveResult result = ConfigurationSaveTransaction.Execute(plan, backupRoot);
        studio.AcceptSaved(path, plan);

        Exception? settingsFailure = null;
        try
        {
            await runtime.AdoptStudioUserSettingsAsync(plan).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            settingsFailure = exception;
        }

        return new ConfigurationStudioSaveExecution(plan, result, settingsFailure);
    }
}
