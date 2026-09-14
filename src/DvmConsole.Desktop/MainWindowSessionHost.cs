// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Specialized;
using DvmConsole.Application;
using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

/// <summary>Desktop bindings for the shared asynchronous session owner.</summary>
internal sealed class MainWindowSessionHost : IAsyncDisposable
{
    private readonly ConsoleSessionHost runtime;
    private readonly Func<MainWindowViewModel, Attachment> attach;

    public MainWindowSessionHost(
        MainWindowViewModel initialViewModel,
        NotifyCollectionChangedEventHandler activityHistoryChanging,
        Action<MainWindowViewModel> setDataContext,
        Action closeSessionWindows,
        Action closeAllWindows,
        Func<MainWindowViewModel, CancellationToken, Task>? quiesceSession = null,
        Action<MainWindowViewModel>? suppressLiveReceiveOutput = null,
        Func<MainWindowViewModel, IConsoleSessionConnectionLifecycle>? createConnectionLifecycle = null,
        TimeSpan? transitionTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(initialViewModel);
        ArgumentNullException.ThrowIfNull(activityHistoryChanging);
        ArgumentNullException.ThrowIfNull(setDataContext);
        Func<MainWindowViewModel, CancellationToken, Task> quiesce = quiesceSession ??
            ((owner, token) => owner.QuiesceFneSessionAsync(token));
        Func<MainWindowViewModel, IConsoleSessionConnectionLifecycle> connections = createConnectionLifecycle ??
            (owner => new ConsoleSessionConnectionLifecycle(owner, quiesce));
        Action<MainWindowViewModel> suppress = suppressLiveReceiveOutput ??
            (owner => owner.SuppressLiveReceiveOutputForShutdown());
        attach = owner => new Attachment(owner, activityHistoryChanging, connections, suppress);
        runtime = new ConsoleSessionHost(attach(initialViewModel),
            binding => setDataContext(((Attachment)binding).Owner), closeSessionWindows, closeAllWindows, transitionTimeout);
        runtime.ReplacementFollowUpFailed += (_, failure) => PublishFollowUpFailure(new(
            ((Attachment)failure.Active).Owner, failure.Phase, failure.Exception));
    }

    private Attachment Current => (Attachment)runtime.Active;
    public MainWindowViewModel ViewModel => Current.Owner;
    public IConsoleApplicationSession ApplicationSession => Current.ApplicationSession;
    public ChannelPttController ChannelPtt => Current.CardPtt!;
    public event EventHandler<SessionReplacementFollowUpFailure>? ReplacementFollowUpFailed;
    public ValueTask StartAsync() => runtime.StartAsync();
    public Task FlushSettingsIfActiveAsync(CancellationToken cancellationToken = default)
        => runtime.FlushSettingsIfActiveAsync(cancellationToken);
    public Task PrepareForReplacementAsync(CancellationToken cancellationToken = default)
        => runtime.PrepareForReplacementAsync(cancellationToken);
    public Task ReplaceAsync(MainWindowViewModel replacement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        return runtime.ReplaceAsync(attach(replacement), cancellationToken);
    }
    public ValueTask DisposeAsync() => runtime.DisposeAsync();

    private sealed class Attachment(
        MainWindowViewModel owner, NotifyCollectionChangedEventHandler historyChanging,
        Func<MainWindowViewModel, IConsoleSessionConnectionLifecycle> createConnections,
        Action<MainWindowViewModel> suppressOutput) : IConsoleSessionAttachment
    {
        private IConsoleApplicationSession? session;
        private IConsoleSessionConnectionLifecycle? connections;
        private bool historySubscribed;
        public MainWindowViewModel Owner { get; } = owner;
        public ChannelPttController? CardPtt { get; private set; }
        public IConsoleApplicationSession ApplicationSession => session!;
        public void InitializeBindings()
        {
            connections = createConnections(Owner);
            session = new ConsoleApplicationSession(new DesktopConsoleSessionRuntimeAdapter(
                Owner, connections.QuiesceAsync,
                token => new ValueTask(Owner.FlushUserSettingsAsync().WaitAsync(token))));
            CardPtt = new ChannelPttController(session.Commands.BeginPttAsync, session.Commands.EndPttAsync);
            Owner.ActivityCallHistoryChanging += historyChanging;
            historySubscribed = true;
        }
        public void DetachBindings()
        {
            if (!historySubscribed) return;
            Owner.ActivityCallHistoryChanging -= historyChanging;
            historySubscribed = false;
        }
        public Task PrepareAsync(CancellationToken token) => Owner.PrepareBackgroundAssetAsync(token);
        public Task StartManualInputsAsync(CancellationToken token) => Owner.StartKeyboardPttAsync(token).AsTask();
        public Task StopManualInputsAsync(CancellationToken token) => Owner.StopKeyboardPttAsync(token).AsTask();
        public void StopAdmission() => Owner.SuppressSessionInputForTransition();
        public void ResumeAdmission() => Owner.ResumeSessionInputAfterFailedTransition();
        public void SuppressLiveOutput() => suppressOutput(Owner);
        public IReadOnlyList<SystemId> CaptureActiveSystemIds() => connections!.CaptureActiveSystemIds();
        public ValueTask RestoreConnectionsAsync(IReadOnlyList<SystemId> systems, CancellationToken token)
            => connections!.RestoreAsync(systems, token);
        public Task RestorePlaybackAsync() => Owner.RestoreSelectedWebStreamsForSessionAsync();
        public ValueTask DisposeSessionAsync() => session?.DisposeAsync() ?? ValueTask.CompletedTask;
        public ValueTask DisposeManualControlsAsync() => CardPtt?.DisposeAsync() ?? ValueTask.CompletedTask;
        public ValueTask DisposeOwnerAsync() => Owner.DisposeAsync();
    }

    private void PublishFollowUpFailure(SessionReplacementFollowUpFailure failure)
    {
        foreach (EventHandler<SessionReplacementFollowUpFailure> observer in ReplacementFollowUpFailed?.GetInvocationList() ?? [])
        {
            try { observer(this, failure); }
            catch { /* Reporting cannot undo completed publication. */ }
        }
    }
}

internal sealed class SessionReplacementFollowUpFailure(
    MainWindowViewModel activeViewModel, SessionReplacementFollowUpPhase phase, Exception exception) : EventArgs
{
    public MainWindowViewModel ActiveViewModel { get; } = activeViewModel ?? throw new ArgumentNullException(nameof(activeViewModel));
    public SessionReplacementFollowUpPhase Phase { get; } = phase;
    public Exception Exception { get; } = exception ?? throw new ArgumentNullException(nameof(exception));
}
