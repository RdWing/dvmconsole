using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;

namespace DvmConsole.Desktop;

/// <summary>
/// Owns serialization, latest-snapshot scheduling, flushes, and Studio rebases
/// for the single live operator-settings instance.
/// </summary>
internal sealed class UserSettingsPersistenceCoordinator : IAsyncDisposable
{
    private readonly UserSettingsStore store;
    private readonly UserSettings settings;
    private readonly LatestUserSettingsWriter writer;

    public UserSettingsPersistenceCoordinator(
        UserSettingsStore store,
        UserSettings settings,
        Action<Exception>? faultHandler = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        writer = new LatestUserSettingsWriter(store.SaveSnapshot, faultHandler);
    }

    public void Schedule()
        => writer.Schedule(store.CaptureSnapshot(settings));

    public Task FlushAsync()
        => writer.FlushAsync();

    public async Task AdoptStudioSnapshotAsync(ConfigurationSavePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ConfigurationFileChange settingsChange = plan.Files.Single(change =>
            change.Category.Equals("Operator settings", StringComparison.Ordinal));
        store.ApplySerializedSnapshot(settings, settingsChange.Content);
        Schedule();
        await FlushAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
        => writer.DisposeAsync();
}
