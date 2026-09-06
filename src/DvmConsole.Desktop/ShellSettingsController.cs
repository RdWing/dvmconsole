// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;

namespace DvmConsole.Desktop;

internal interface IShellSettingsSessionPort
{
    bool IsClosed { get; }
    void CaptureConfigurationState();
    void NotifyProfilesChanged();
    void PublishStatus(string text);
    void ReportPersistenceFailure(Exception exception);
}

internal sealed class ShellSettingsSessionPort(
    Func<bool> isClosed,
    Action captureConfigurationState,
    Action notifyProfilesChanged,
    Action<string> publishStatus,
    Action<Exception> reportPersistenceFailure) : IShellSettingsSessionPort
{
    public bool IsClosed => isClosed();
    public void CaptureConfigurationState() => captureConfigurationState();
    public void NotifyProfilesChanged() => notifyProfilesChanged();
    public void PublishStatus(string text) => publishStatus(text);
    public void ReportPersistenceFailure(Exception exception) => reportPersistenceFailure(exception);
}

/// <summary>
/// Owns operator-settings serialization, profile commands, and the latest-write
/// lifetime while the main view model retains its binding-compatible facade.
/// </summary>
internal sealed class ShellSettingsController : IAsyncDisposable
{
    private readonly UserSettingsStore store;
    private readonly UserSettings settings;
    private readonly UserSettingsPersistenceCoordinator persistence;
    private readonly IShellSettingsSessionPort session;

    public ShellSettingsController(
        UserSettingsStore store,
        UserSettings settings,
        IShellSettingsSessionPort session,
        IUiDispatcher? dispatcher = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        persistence = new UserSettingsPersistenceCoordinator(store, settings,
            session.ReportPersistenceFailure, dispatcher, session.CaptureConfigurationState);
    }

    public IReadOnlyList<string> NamedProfiles => store.ListNamedProfiles();

    public void Export(string path)
    {
        session.CaptureConfigurationState();
        store.Export(settings, path);
    }
    public void Export(Stream destination)
    {
        session.CaptureConfigurationState();
        store.Export(settings, destination);
    }
    public SettingsImportPreview PreviewImport(string path) => store.PreviewImport(path);
    public SettingsImportStage StageImport(Stream source, string sourceName)
        => store.StageImport(source, sourceName);
    public SettingsImportPreview PreviewNamedProfile(string profileName)
        => store.PreviewNamedProfile(profileName);
    public SettingsImportStage StageNamedProfile(string profileName)
        => store.StageNamedProfile(profileName);
    public void Import(string path, SettingsImportScope scope) => store.Import(path, scope);
    public void Import(Stream source, SettingsImportScope scope) => store.Import(source, scope);
    public void Import(SettingsImportStage stage, SettingsImportScope scope, bool acceptRecordingPolicy)
        => store.Import(stage, scope, acceptRecordingPolicy);

    public void SaveNamedProfile(string profileName)
    {
        session.CaptureConfigurationState();
        store.SaveNamedProfile(profileName, settings);
        session.NotifyProfilesChanged();
        session.PublishStatus($"Settings profile '{profileName.Trim()}' saved.");
    }

    public void ImportNamedProfile(string profileName, SettingsImportScope scope)
    {
        store.ImportNamedProfile(profileName, scope);
        session.PublishStatus($"Settings profile '{profileName.Trim()}' imported.");
    }

    public void ImportNamedProfile(
        string profileName,
        SettingsImportStage stage,
        SettingsImportScope scope,
        bool acceptRecordingPolicy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        store.Import(stage, scope, acceptRecordingPolicy);
        session.PublishStatus($"Settings profile '{profileName.Trim()}' imported.");
    }

    public void DeleteNamedProfile(string profileName)
    {
        store.DeleteNamedProfile(profileName);
        session.NotifyProfilesChanged();
        session.PublishStatus($"Settings profile '{profileName.Trim()}' deleted.");
    }

    public void Reset() => store.Reset();

    public void Schedule()
    {
        if (session.IsClosed)
            return;

        try
        {
            persistence.Schedule();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException ||
            exception is ObjectDisposedException && session.IsClosed)
        {
            // Operator state must never prevent the console from running.
        }
    }

    public Task FlushAsync() => persistence.FlushAsync();
    public Task AdoptStudioSnapshotAsync(ConfigurationSavePlan plan)
        => persistence.AdoptStudioSnapshotAsync(plan);
    public Task AdoptSnapshotAsync(UserSettingsSnapshot snapshot)
        => persistence.AdoptSnapshotAsync(snapshot);
    public ValueTask DisposeAsync() => persistence.DisposeAsync();
}
