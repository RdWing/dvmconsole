// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using DvmConsole.Application;
using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

internal sealed class MainWindowSessionHost : IAsyncDisposable
{
    private static readonly TimeSpan DefaultTransitionTimeout = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim transitionGate = new(1, 1);
    private readonly NotifyCollectionChangedEventHandler activityHistoryChanging;
    private readonly Action<MainWindowViewModel> setDataContext;
    private readonly Action closeSessionWindows;
    private readonly Action closeAllWindows;
    private readonly Func<MainWindowViewModel, CancellationToken, Task> quiesceSession;
    private readonly Func<MainWindowViewModel, IConsoleSessionConnectionLifecycle> createConnectionLifecycle;
    private readonly Action<MainWindowViewModel> suppressLiveReceiveOutput;
    private readonly TimeSpan transitionTimeout;
    private readonly AsyncDisposal disposal = new();
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private MainWindowViewModel viewModel;
    private IConsoleApplicationSession applicationSession;
    private IConsoleSessionConnectionLifecycle connectionLifecycle;
    private ChannelPttController cardPtt;
    private int disposalStarted;

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
        viewModel = initialViewModel ?? throw new ArgumentNullException(nameof(initialViewModel));
        ArgumentNullException.ThrowIfNull(activityHistoryChanging);
        this.activityHistoryChanging = activityHistoryChanging;
        this.setDataContext = setDataContext ?? throw new ArgumentNullException(nameof(setDataContext));
        this.closeSessionWindows = closeSessionWindows ?? throw new ArgumentNullException(nameof(closeSessionWindows));
        this.closeAllWindows = closeAllWindows ?? throw new ArgumentNullException(nameof(closeAllWindows));
        this.quiesceSession = quiesceSession ?? QuiesceSessionAsync;
        this.createConnectionLifecycle = createConnectionLifecycle ??
            (owner => new ConsoleSessionConnectionLifecycle(owner, this.quiesceSession));
        this.suppressLiveReceiveOutput = suppressLiveReceiveOutput ??
            (owner => owner.SuppressLiveReceiveOutputForShutdown());
        this.transitionTimeout = transitionTimeout ?? DefaultTransitionTimeout;
        if (this.transitionTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(transitionTimeout));
        connectionLifecycle = this.createConnectionLifecycle(viewModel);
        applicationSession = CreateApplicationSession(viewModel, connectionLifecycle);
        cardPtt = CreateChannelPtt(applicationSession);
        viewModel.ActivityCallHistoryChanging += activityHistoryChanging;
        this.setDataContext(viewModel);
    }

    public MainWindowViewModel ViewModel => viewModel;
    public IConsoleApplicationSession ApplicationSession => applicationSession;
    public ChannelPttController ChannelPtt => cardPtt;

    public event EventHandler<SessionReplacementFollowUpFailure>? ReplacementFollowUpFailed;

    public async ValueTask StartAsync()
    {
        CancellationToken cancellationToken = lifetimeCancellation.Token;
        await viewModel.PrepareBackgroundAssetAsync(cancellationToken).ConfigureAwait(false);
        await viewModel.StartKeyboardPttAsync(cancellationToken).ConfigureAwait(false);
        await viewModel.RestoreSelectedWebStreamsForSessionAsync()
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    // Window deactivation can overlap a configuration replacement or final
    // shutdown. Keep the settings writer inside the same ownership gate as
    // those transitions so it can never outlive the session being flushed.
    public async Task FlushSettingsIfActiveAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref disposalStarted) != 0)
            return;

        await transitionGate.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref disposalStarted) == 0)
                await applicationSession.FlushSettingsAsync(cancellationToken);
        }
        finally
        {
            transitionGate.Release();
        }
    }

    // A replacement session reloads operator settings from the shared store.
    // Flush the outgoing session before constructing that replacement so two
    // session-owned writers never race over different settings snapshots.
    public async Task PrepareForReplacementAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposalStarted) != 0, this);
        await transitionGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposalStarted) != 0, this);
            await applicationSession.FlushSettingsAsync(cancellationToken);
        }
        finally
        {
            transitionGate.Release();
        }
    }

    public async Task ReplaceAsync(
        MainWindowViewModel replacement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (Volatile.Read(ref disposalStarted) != 0)
        {
            await replacement.DisposeAsync();
            throw new ObjectDisposedException(nameof(MainWindowSessionHost));
        }

        using var transitionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetimeCancellation.Token);
        CancellationToken forwardCancellation = transitionCancellation.Token;
        try
        {
            await transitionGate.WaitAsync(forwardCancellation);
        }
        catch
        {
            await replacement.DisposeAsync();
            throw;
        }
        var followUpFailures = new List<SessionReplacementFollowUpFailure>();
        using var transitionDeadline = new SessionTransitionDeadline(transitionTimeout);
        try
        {
            if (Volatile.Read(ref disposalStarted) != 0)
            {
                await replacement.DisposeAsync();
                throw new ObjectDisposedException(nameof(MainWindowSessionHost));
            }

            MainWindowViewModel previous = viewModel;
            IConsoleApplicationSession previousApplicationSession = applicationSession;
            IConsoleSessionConnectionLifecycle previousConnectionLifecycle = connectionLifecycle;
            ChannelPttController previousCardPtt = cardPtt;
            IConsoleSessionConnectionLifecycle? replacementConnectionLifecycle = null;
            IConsoleApplicationSession? replacementApplicationSession = null;
            ChannelPttController? replacementCardPtt = null;
            IReadOnlyList<SystemId> previousActiveSystems = [];
            bool replacementHistorySubscribed = false;
            bool previousPttStopped = false;
            try
            {
                previousActiveSystems = previousConnectionLifecycle.CaptureActiveSystemIds();
                replacementConnectionLifecycle = createConnectionLifecycle(replacement);
                replacementApplicationSession = CreateApplicationSession(
                    replacement,
                    replacementConnectionLifecycle);
                replacementCardPtt = CreateChannelPtt(replacementApplicationSession);
                replacement.ActivityCallHistoryChanging += activityHistoryChanging;
                replacementHistorySubscribed = true;
                await transitionDeadline.RunAsync(
                    replacement.PrepareBackgroundAssetAsync,
                    forwardCancellation);
                previous.SuppressSessionInputForTransition();
                await transitionDeadline.RunAsync(
                    token => previous.StopKeyboardPttAsync(token).AsTask(),
                    forwardCancellation);
                previousPttStopped = true;
                // The outgoing session can take time to release audio,
                // recording, and presentation resources. Close its FNE
                // transports before publishing the replacement so the same
                // peer identity can never be active from two local sockets.
                await transitionDeadline.RunAsync(
                    token => previousApplicationSession.QuiesceAsync(token).AsTask(),
                    forwardCancellation);
                await transitionDeadline.RunAsync(
                    token => replacement.StartKeyboardPttAsync(token).AsTask(),
                    forwardCancellation);
                closeSessionWindows();
                setDataContext(replacement);
            }
            catch (Exception replacementFailure)
            {
                var rollback = new AsyncCleanup();
                if (replacementHistorySubscribed)
                    rollback.Run(() => replacement.ActivityCallHistoryChanging -= activityHistoryChanging);
                if (replacementApplicationSession is not null)
                {
                    await rollback.RunTaskAsync(() => transitionDeadline.RunAsync(
                        _ => replacementApplicationSession.DisposeAsync().AsTask(),
                        CancellationToken.None));
                }
                if (replacementCardPtt is not null)
                {
                    await rollback.RunTaskAsync(() => transitionDeadline.RunAsync(
                        _ => replacementCardPtt.DisposeAsync().AsTask(),
                        CancellationToken.None));
                }
                await rollback.RunTaskAsync(() => transitionDeadline.RunAsync(
                    _ => replacement.DisposeAsync().AsTask(),
                    CancellationToken.None));
                if (previousPttStopped)
                {
                    await rollback.RunTaskAsync(
                        () => transitionDeadline.RunAsync(
                            token => previous.StartKeyboardPttAsync(token).AsTask(),
                            CancellationToken.None));
                }
                await rollback.RunTaskAsync(
                    () => transitionDeadline.RunAsync(
                        token => previousConnectionLifecycle
                            .RestoreAsync(previousActiveSystems, token)
                            .AsTask(),
                        CancellationToken.None));
                rollback.Run(previous.ResumeSessionInputAfterFailedTransition);
                try
                {
                    rollback.ThrowIfFailed();
                }
                catch (Exception rollbackFailure)
                {
                    throw new InvalidOperationException(
                        $"The replacement session could not be activated: {replacementFailure.Message} " +
                        "Restoring the previous session also failed; restart DVM Console before continuing.",
                        new AggregateException(replacementFailure, rollbackFailure));
                }

                if (replacementFailure is OperationCanceledException)
                    ExceptionDispatchInfo.Capture(replacementFailure).Throw();
                throw new InvalidOperationException(
                    $"The replacement session could not be activated: {replacementFailure.Message}",
                    replacementFailure);
            }

            // Ownership changes only after the replacement is ready and the
            // window has accepted it. Cleanup failures from the outgoing
            // session cannot leave the host pointing at a half-installed one.
            viewModel = replacement;
            applicationSession = replacementApplicationSession!;
            connectionLifecycle = replacementConnectionLifecycle!;
            cardPtt = replacementCardPtt!;
            previous.ActivityCallHistoryChanging -= activityHistoryChanging;

            var retiredSessionCleanup = new AsyncCleanup();
            await retiredSessionCleanup.RunTaskAsync(
                () => transitionDeadline.RunAsync(
                    _ => previousApplicationSession.DisposeAsync().AsTask(),
                    CancellationToken.None));
            await retiredSessionCleanup.RunTaskAsync(
                () => transitionDeadline.RunAsync(
                    _ => previousCardPtt.DisposeAsync().AsTask(),
                    CancellationToken.None));
            await retiredSessionCleanup.RunTaskAsync(() => transitionDeadline.RunAsync(
                _ => previous.DisposeAsync().AsTask(),
                CancellationToken.None));
            CaptureFollowUpFailureIfAny(
                followUpFailures,
                replacement,
                SessionReplacementFollowUpPhase.RetiredSessionCleanup,
                retiredSessionCleanup);

            try
            {
                await transitionDeadline.RunAsync(
                    token => replacement.RestoreSelectedWebStreamsForSessionAsync().WaitAsync(token),
                    CancellationToken.None);
            }
            catch (Exception exception)
            {
                followUpFailures.Add(new SessionReplacementFollowUpFailure(
                    replacement,
                    SessionReplacementFollowUpPhase.SelectedWebStreamRestore,
                    exception));
            }
        }
        finally
        {
            transitionGate.Release();
        }

        foreach (SessionReplacementFollowUpFailure failure in followUpFailures)
            PublishFollowUpFailure(failure);
    }

    public ValueTask DisposeAsync()
        => disposal.RunAsync(DisposeCoreAsync);

    private async Task DisposeCoreAsync()
    {
        Volatile.Write(ref disposalStarted, 1);
        lifetimeCancellation.Cancel();
        var cleanup = new AsyncCleanup();
        using var shutdownDeadline = new SessionTransitionDeadline(transitionTimeout);
        bool transitionEntered = false;
        // Silence live FNE audio before waiting for a concurrent settings or
        // replacement transition. Full route disposal can then drain in its
        // normal ownership order without keeping speakers active meanwhile.
        MainWindowViewModel earlySuppressedViewModel = viewModel;
        earlySuppressedViewModel.SuppressSessionInputForTransition();
        cleanup.Run(() => suppressLiveReceiveOutput(earlySuppressedViewModel));
        try
        {
            await shutdownDeadline.WaitAsync(
                transitionGate,
                CancellationToken.None).ConfigureAwait(false);
            transitionEntered = true;
            if (!ReferenceEquals(viewModel, earlySuppressedViewModel))
            {
                viewModel.SuppressSessionInputForTransition();
                cleanup.Run(() => suppressLiveReceiveOutput(viewModel));
            }
            // Stop network sessions before the rest of the session graph. In
            // particular, this cancels a failed peer's login backoff without
            // making application shutdown wait for the current retry interval.
            await cleanup.RunTaskAsync(
                () => shutdownDeadline.RunAsync(
                    token => applicationSession.QuiesceAsync(token).AsTask(),
                    CancellationToken.None));
            cleanup.Run(closeAllWindows);
            cleanup.Run(() => viewModel.ActivityCallHistoryChanging -= activityHistoryChanging);
            await cleanup.RunTaskAsync(() => shutdownDeadline.RunAsync(
                _ => applicationSession.DisposeAsync().AsTask(),
                CancellationToken.None));
            await cleanup.RunTaskAsync(() => shutdownDeadline.RunAsync(
                _ => cardPtt.DisposeAsync().AsTask(),
                CancellationToken.None));
            await cleanup.RunTaskAsync(() => shutdownDeadline.RunAsync(
                _ => viewModel.DisposeAsync().AsTask(),
                CancellationToken.None));
            cleanup.ThrowIfFailed();
        }
        finally
        {
            if (transitionEntered)
                transitionGate.Release();
            lifetimeCancellation.Dispose();
        }
    }

    private static ChannelPttController CreateChannelPtt(IConsoleApplicationSession session)
        => new(
            (channelId, cancellationToken) => session.Commands.BeginPttAsync(channelId, cancellationToken),
            (channelId, cancellationToken) => session.Commands.EndPttAsync(channelId, cancellationToken));

    private static IConsoleApplicationSession CreateApplicationSession(
        MainWindowViewModel owner,
        IConsoleSessionConnectionLifecycle connectionLifecycle)
        => new ConsoleApplicationSession(new DesktopConsoleSessionRuntimeAdapter(
            owner,
            connectionLifecycle.QuiesceAsync,
            cancellationToken => new ValueTask(owner.FlushUserSettingsAsync().WaitAsync(cancellationToken))));

    private static Task QuiesceSessionAsync(
        MainWindowViewModel owner,
        CancellationToken cancellationToken)
        => owner.QuiesceFneSessionAsync(cancellationToken);

    private static void CaptureFollowUpFailureIfAny(
        ICollection<SessionReplacementFollowUpFailure> failures,
        MainWindowViewModel activeViewModel,
        SessionReplacementFollowUpPhase phase,
        AsyncCleanup cleanup)
    {
        try
        {
            cleanup.ThrowIfFailed();
        }
        catch (Exception exception)
        {
            failures.Add(new SessionReplacementFollowUpFailure(
                activeViewModel,
                phase,
                exception));
        }
    }

    private void PublishFollowUpFailure(SessionReplacementFollowUpFailure args)
    {
        Delegate[] observers = ReplacementFollowUpFailed?.GetInvocationList() ?? [];
        foreach (Delegate observer in observers)
        {
            try
            {
                ((EventHandler<SessionReplacementFollowUpFailure>)observer)(this, args);
            }
            catch
            {
                // Follow-up reporting must never turn a completed replacement
                // back into an operator-visible activation failure.
            }
        }
    }
}

// One monotonic deadline covers prepare, commit, rollback, and retired-session
// cleanup. Caller cancellation can stop the forward operation, but rollback
// uses the remaining transition budget so it is still attempted.
internal sealed class SessionTransitionDeadline : IDisposable
{
    private readonly TimeSpan timeout;
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly CancellationTokenSource deadline = new();

    public SessionTransitionDeadline(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        this.timeout = timeout;
        deadline.CancelAfter(timeout);
    }

    public async Task RunAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken callerCancellation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var stepCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token,
            callerCancellation);
        // Start every independent cleanup owner even when an earlier phase has
        // exhausted the wait budget. A closed deadline then abandons and
        // observes that work instead of silently skipping its safety actions.
        Task task = Task.Run(
            async () => await operation(stepCancellation.Token).ConfigureAwait(false),
            CancellationToken.None);
        try
        {
            await task.WaitAsync(stepCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            deadline.IsCancellationRequested && !callerCancellation.IsCancellationRequested)
        {
            TaskObservation.Observe(task);
            throw new TimeoutException("The session transition exceeded its overall deadline.", exception);
        }
        catch
        {
            if (!task.IsCompleted)
                TaskObservation.Observe(task);
            throw;
        }
    }

    public async Task WaitAsync(
        SemaphoreSlim gate,
        CancellationToken callerCancellation)
    {
        ArgumentNullException.ThrowIfNull(gate);
        TimeSpan remaining = timeout - Stopwatch.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero)
            throw new TimeoutException("The session transition exceeded its overall deadline.");

        using var stepCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token,
            callerCancellation);
        try
        {
            await gate.WaitAsync(stepCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            deadline.IsCancellationRequested && !callerCancellation.IsCancellationRequested)
        {
            throw new TimeoutException("The session transition exceeded its overall deadline.", exception);
        }
    }

    public void Dispose() => deadline.Dispose();
}

internal enum SessionReplacementFollowUpPhase
{
    RetiredSessionCleanup,
    SelectedWebStreamRestore
}

internal sealed class SessionReplacementFollowUpFailure(
    MainWindowViewModel activeViewModel,
    SessionReplacementFollowUpPhase phase,
    Exception exception) : EventArgs
{
    public MainWindowViewModel ActiveViewModel { get; } =
        activeViewModel ?? throw new ArgumentNullException(nameof(activeViewModel));

    public SessionReplacementFollowUpPhase Phase { get; } = phase;

    public Exception Exception { get; } =
        exception ?? throw new ArgumentNullException(nameof(exception));
}
