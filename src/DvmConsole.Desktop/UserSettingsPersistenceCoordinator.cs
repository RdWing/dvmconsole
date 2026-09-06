// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

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
    private readonly CoalescedUiAction? capture;
    private readonly IUiDispatcher? dispatcher;
    private readonly Action? beforeCapture;
    private Exception? captureFailure;

    public UserSettingsPersistenceCoordinator(
        UserSettingsStore store,
        UserSettings settings,
        Action<Exception>? faultHandler = null,
        IUiDispatcher? dispatcher = null,
        Action? beforeCapture = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        writer = new LatestUserSettingsWriter(store.SaveSnapshot, faultHandler);
        this.beforeCapture = beforeCapture;
        this.dispatcher = dispatcher;
        if (dispatcher is not null)
            capture = new CoalescedUiAction(dispatcher, Capture, faultHandler);
    }

    public void Schedule()
    {
        if (capture is null)
            Capture();
        else
            capture.Schedule();
    }

    private void Capture()
    {
        try
        {
            beforeCapture?.Invoke();
            writer.Schedule(store.CaptureSnapshot(settings));
            captureFailure = null;
        }
        catch (Exception exception)
        {
            captureFailure = exception;
            throw;
        }
    }

    public async Task FlushAsync()
    {
        if (capture is not null)
            await capture.FlushAsync().ConfigureAwait(false);
        if (captureFailure is not null)
            throw new InvalidOperationException("The latest settings snapshot could not be captured.", captureFailure);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    public Task AdoptStudioSnapshotAsync(ConfigurationSavePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ConfigurationFileChange settingsChange = plan.Files.Single(change =>
            change.Category.Equals("Operator settings", StringComparison.Ordinal));
        return AdoptSerializedSnapshotAsync(settingsChange.Content);
    }

    public async Task AdoptSnapshotAsync(UserSettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await AdoptSerializedSnapshotAsync(snapshot.Json).ConfigureAwait(false);
    }

    private async Task AdoptSerializedSnapshotAsync(string json)
    {
        void Adopt()
        {
            store.ApplySerializedSnapshot(settings, json);
            Schedule();
        }
        if (dispatcher is null)
            Adopt();
        else
            await dispatcher.InvokeAsync(Adopt).ConfigureAwait(false);
        await FlushAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            capture?.Dispose();
            await writer.DisposeAsync().ConfigureAwait(false);
        }
    }
}
