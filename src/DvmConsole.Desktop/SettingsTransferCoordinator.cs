// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Settings;

namespace DvmConsole.Desktop;

internal interface ISettingsTransferSession
{
    bool IsCurrent { get; }
    Task FlushAsync(CancellationToken cancellationToken);
    void Import(SettingsImportStage stage, SettingsImportScope scope, bool acceptRecordingPolicy, string? profileName);
    void Reset();
    Task ReloadAsync();
}

/// <summary>
/// Orders operator-settings commits and session reloads. The window supplies
/// an already reviewed stage; file pickers and confirmations remain in the UI.
/// </summary>
internal sealed class SettingsTransferCoordinator(Func<ISettingsTransferSession> captureSession)
{
    private readonly SemaphoreSlim transferGate = new(1, 1);

    public Task ImportAsync(
        SettingsImportStage stage,
        SettingsImportScope scope = SettingsImportScope.All,
        bool acceptRecordingPolicy = false,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return TransferAsync(
            session => session.Import(stage, scope, acceptRecordingPolicy, profileName),
            cancellationToken);
    }

    public Task ResetAsync(CancellationToken cancellationToken = default)
        => TransferAsync(session => session.Reset(), cancellationToken);

    private async Task TransferAsync(Action<ISettingsTransferSession> commit, CancellationToken cancellationToken)
    {
        // Bind to the configuration the operator reviewed, even if another
        // settings operation or a library activation wins the transition first.
        ISettingsTransferSession session = captureSession();
        await transferGate.WaitAsync(cancellationToken);
        try
        {
            EnsureCurrent(session);
            await session.FlushAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCurrent(session);
            commit(session);
            try
            {
                // Once committed, finish adoption rather than leaving a
                // caller-canceled reload paired with the old live settings.
                await session.ReloadAsync();
            }
            catch (Exception exception)
            {
                throw new IOException(
                    "Settings were saved, but the current configuration could not be reloaded. " +
                    "Restart DVM Console to load the saved settings. " + exception.Message, exception);
            }
        }
        finally { transferGate.Release(); }
    }

    private static void EnsureCurrent(ISettingsTransferSession session)
    {
        if (!session.IsCurrent)
            throw new IOException("The active configuration changed. Preview the settings again before applying them.");
    }
}

internal sealed class DesktopSettingsTransferSession(
    MainWindowViewModel owner,
    MainWindowSessionHost host,
    Func<MainWindowViewModel> loadReplacement,
    Func<MainWindowViewModel, Task> replace) : ISettingsTransferSession
{
    public bool IsCurrent => ReferenceEquals(host.ViewModel, owner);
    public Task FlushAsync(CancellationToken cancellationToken) => host.PrepareForReplacementAsync(cancellationToken);
    public void Import(SettingsImportStage stage, SettingsImportScope scope, bool acceptRecordingPolicy, string? profileName)
    {
        if (profileName is null)
            owner.ImportSettings(stage, scope, acceptRecordingPolicy);
        else
            owner.ImportNamedSettingsProfile(profileName, stage, scope, acceptRecordingPolicy);
    }
    public void Reset() => owner.ResetSettings();
    public Task ReloadAsync() => replace(loadReplacement());
}
