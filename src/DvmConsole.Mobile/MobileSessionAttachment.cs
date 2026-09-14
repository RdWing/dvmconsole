// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;

namespace DvmConsole.Mobile;

/// <summary>The host supplies services; mobile owns only presentation and attachment lifetime.</summary>
public sealed record MobileSession(IConsoleApplicationSession Application,
    IConsoleConnectionCommands? Connections = null, Func<IReadOnlyList<SystemId>>? ActiveSystems = null, Func<CancellationToken, Task>? ResumeListening = null, Func<CancellationToken, Task>? ActivateListening = null, Func<Stream, CancellationToken, Task>? ExportHistory = null, IConsoleRecordingArchive? RecordingArchive = null)
{
    public DvmConsole.Presentation.DebugLogWorkspace? Diagnostics { get; init; }
    public ConsoleExecutionPolicy? Execution { get; init; }
    public IConsoleToneSettingsStore? ToneSettings { get; init; }
    public IConsoleConnectionStartupPreferences? ConnectionStartup { get; init; }
    public IAssetStore? Assets { get; init; }
}

internal sealed class MobileSessionAttachment : IConsoleSessionAttachment
{
    private readonly MobileConsoleView view;
    private readonly MobileSession session;
    private readonly MobileSessionDiagnostics diagnostics;
    private readonly ListeningActivationCoordinator? activation;

    public MobileSessionAttachment(MobileConsoleView view, MobileSession session)
    {
        if (session.Execution is { } execution && !ReferenceEquals(execution, view.Execution))
            throw new ArgumentException("The console view must observe its session's execution policy.", nameof(session));
        this.view = view;
        diagnostics = new MobileSessionDiagnostics(session.Application);
        activation = session.ActivateListening is { } activate
            ? new ListeningActivationCoordinator(activate, session.ResumeListening) : null;
        this.session = session with
        {
            Diagnostics = diagnostics.Workspace,
            Execution = session.Execution ?? view.Execution,
            ActivateListening = activation is null ? session.ActivateListening : activation.ActivateAsync,
            ResumeListening = activation is null ? session.ResumeListening : activation.ResumeAsync
        };
    }

    public MobileConsoleView View => view;
    public MobileSession Session => session;
    public IConsoleApplicationSession ApplicationSession => session.Application;
    public void InitializeBindings() { }
    public void DetachBindings() { }
    public Task PrepareAsync(CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    public Task StartManualInputsAsync(CancellationToken cancellationToken) => PrepareAsync(cancellationToken);
    public Task StopManualInputsAsync(CancellationToken cancellationToken) => view.ReleaseManualTransmitAsync().AsTask();
    public void StopAdmission()
    {
        (session.Application as IConsoleSessionInputAdmission)?.SuspendInput();
        view.SetAdmission(false);
    }
    public void ResumeAdmission() => view.SetAdmission(true);
    public void SuppressLiveOutput() { }
    public IReadOnlyList<SystemId> CaptureActiveSystemIds() => session.ActiveSystems?.Invoke() ?? [];
    public ValueTask RestoreConnectionsAsync(IReadOnlyList<SystemId> systems, CancellationToken cancellationToken)
        => new(session.Connections?.RestoreAsync(systems, cancellationToken) ?? Task.CompletedTask);
    public Task RestorePlaybackAsync() => session.ActivateListening?.Invoke(CancellationToken.None) ?? Task.CompletedTask;
    public async ValueTask DisposeSessionAsync()
    {
        var cleanup = new AsyncCleanup();
        cleanup.Run(diagnostics.Dispose);
        await cleanup.RunTasksAsync([
            () => activation?.DisposeAsync().AsTask() ?? Task.CompletedTask,
            () => ApplicationSession.DisposeAsync().AsTask()
        ]).ConfigureAwait(false);
        cleanup.ThrowIfFailed();
    }
    public ValueTask DisposeManualControlsAsync() => view.DisposeAsync();
    public async ValueTask DisposeOwnerAsync()
    {
        var cleanup = new AsyncCleanup();
        await cleanup.RunTaskAsync(() => DisposeSessionAsync().AsTask());
        await cleanup.RunTaskAsync(() => view.DisposeAsync().AsTask());
        cleanup.ThrowIfFailed();
    }
}
