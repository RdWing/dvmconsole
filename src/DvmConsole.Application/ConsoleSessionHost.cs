// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.ExceptionServices;

namespace DvmConsole.Application;

/// <summary>Host bindings and input facilities attached to one operational session.</summary>
public interface IConsoleSessionAttachment
{
    IConsoleApplicationSession ApplicationSession { get; }
    void InitializeBindings();
    void DetachBindings();
    Task PrepareAsync(CancellationToken cancellationToken);
    Task StartManualInputsAsync(CancellationToken cancellationToken);
    Task StopManualInputsAsync(CancellationToken cancellationToken);
    void StopAdmission();
    void ResumeAdmission();
    void SuppressLiveOutput();
    IReadOnlyList<SystemId> CaptureActiveSystemIds();
    ValueTask RestoreConnectionsAsync(IReadOnlyList<SystemId> systems, CancellationToken cancellationToken);
    Task RestorePlaybackAsync();
    ValueTask DisposeSessionAsync();
    ValueTask DisposeManualControlsAsync();
    ValueTask DisposeOwnerAsync();
}

public enum SessionReplacementFollowUpPhase { RetiredSessionCleanup, SelectedWebStreamRestore }

public sealed class ConsoleSessionFollowUpFailure(
    IConsoleSessionAttachment active, SessionReplacementFollowUpPhase phase, Exception exception) : EventArgs
{
    public IConsoleSessionAttachment Active { get; } = active;
    public SessionReplacementFollowUpPhase Phase { get; } = phase;
    public Exception Exception { get; } = exception;
}

/// <summary>Owns asynchronous replacement, rollback and final retirement for every host.</summary>
public sealed class ConsoleSessionHost : IAsyncDisposable
{
    private readonly SemaphoreSlim transitionGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly AsyncDisposal disposal = new();
    private readonly TimeSpan timeout;
    private readonly Action<IConsoleSessionAttachment> publish;
    private readonly Action closeSessionPresentation;
    private readonly Action closeAllPresentation;
    private IConsoleSessionAttachment active;
    private int disposalStarted;

    public ConsoleSessionHost(IConsoleSessionAttachment initial, Action<IConsoleSessionAttachment> publish,
        Action closeSessionPresentation, Action closeAllPresentation, TimeSpan? transitionTimeout = null)
    {
        active = initial ?? throw new ArgumentNullException(nameof(initial));
        this.publish = publish ?? throw new ArgumentNullException(nameof(publish));
        this.closeSessionPresentation = closeSessionPresentation ?? throw new ArgumentNullException(nameof(closeSessionPresentation));
        this.closeAllPresentation = closeAllPresentation ?? throw new ArgumentNullException(nameof(closeAllPresentation));
        timeout = transitionTimeout ?? TimeSpan.FromSeconds(10);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(transitionTimeout));
        active.InitializeBindings();
        publish(active);
    }

    public IConsoleSessionAttachment Active => active;
    public event EventHandler<ConsoleSessionFollowUpFailure>? ReplacementFollowUpFailed;

    public async ValueTask StartAsync()
    {
        CancellationToken token = lifetime.Token;
        await active.PrepareAsync(token).ConfigureAwait(false);
        await active.StartManualInputsAsync(token).ConfigureAwait(false);
        await active.RestorePlaybackAsync().WaitAsync(token).ConfigureAwait(false);
    }

    public async Task FlushSettingsIfActiveAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref disposalStarted) != 0) return;
        await transitionGate.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref disposalStarted) == 0)
                await active.ApplicationSession.FlushSettingsAsync(cancellationToken);
        }
        finally { transitionGate.Release(); }
    }

    // Flush before construction so the replacement reads the latest operator choices.
    public async Task PrepareForReplacementAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposalStarted) != 0, this);
        await transitionGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposalStarted) != 0, this);
            await active.ApplicationSession.FlushSettingsAsync(cancellationToken);
        }
        finally { transitionGate.Release(); }
    }

    public async Task ReplaceAsync(IConsoleSessionAttachment replacement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (Volatile.Read(ref disposalStarted) != 0)
        {
            await replacement.DisposeOwnerAsync();
            throw new ObjectDisposedException(nameof(ConsoleSessionHost));
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        CancellationToken forward = linked.Token;
        try { await transitionGate.WaitAsync(forward); }
        catch { await replacement.DisposeOwnerAsync(); throw; }
        var failures = new List<ConsoleSessionFollowUpFailure>();
        using var deadline = new SessionTransitionDeadline(timeout);
        try
        {
            if (Volatile.Read(ref disposalStarted) != 0)
            {
                await replacement.DisposeOwnerAsync();
                throw new ObjectDisposedException(nameof(ConsoleSessionHost));
            }
            IConsoleSessionAttachment previous = active;
            IReadOnlyList<SystemId> previousSystems = [];
            bool previousManualStopped = false;
            try
            {
                previousSystems = previous.CaptureActiveSystemIds();
                replacement.InitializeBindings();
                await deadline.RunAsync(replacement.PrepareAsync, forward);
                previous.StopAdmission();
                await deadline.RunAsync(previous.StopManualInputsAsync, forward);
                previousManualStopped = true;
                // Release transports before publication to avoid duplicate peer sockets.
                await deadline.RunAsync(token => previous.ApplicationSession.QuiesceAsync(token).AsTask(), forward);
                await deadline.RunAsync(replacement.StartManualInputsAsync, forward);
                closeSessionPresentation();
                publish(replacement);
            }
            catch (Exception failure)
            {
                var rollback = new AsyncCleanup();
                rollback.Run(replacement.DetachBindings);
                await DisposeAttachmentAsync(replacement, deadline, rollback);
                bool canRestore = false;
                rollback.Run(() =>
                {
                    (previous.ApplicationSession as IConsoleSessionReactivation)?.ReactivateAfterFailedReplacement();
                    canRestore = true;
                });
                if (canRestore)
                {
                    if (previousManualStopped)
                        await rollback.RunTaskAsync(() => deadline.RunAsync(previous.StartManualInputsAsync, CancellationToken.None));
                    await rollback.RunTaskAsync(() => deadline.RunAsync(
                        token => previous.RestoreConnectionsAsync(previousSystems, token).AsTask(), CancellationToken.None));
                    rollback.Run(previous.ResumeAdmission);
                }
                try { rollback.ThrowIfFailed(); }
                catch (Exception rollbackFailure)
                {
                    throw new InvalidOperationException(
                        $"The replacement session could not be activated: {failure.Message} " +
                        "Restoring the previous session also failed; restart DVM Console before continuing.",
                        new AggregateException(failure, rollbackFailure));
                }
                if (failure is OperationCanceledException) ExceptionDispatchInfo.Capture(failure).Throw();
                throw new InvalidOperationException($"The replacement session could not be activated: {failure.Message}", failure);
            }

            // Publication commits ownership; retired cleanup cannot roll this back.
            active = replacement;
            previous.DetachBindings();
            var retired = new AsyncCleanup();
            await DisposeAttachmentAsync(previous, deadline, retired);
            try { retired.ThrowIfFailed(); }
            catch (Exception exception)
            {
                failures.Add(new(replacement, SessionReplacementFollowUpPhase.RetiredSessionCleanup, exception));
            }
            try
            {
                await deadline.RunAsync(token => replacement.RestorePlaybackAsync().WaitAsync(token), CancellationToken.None);
            }
            catch (Exception exception)
            {
                failures.Add(new(replacement, SessionReplacementFollowUpPhase.SelectedWebStreamRestore, exception));
            }
        }
        finally { transitionGate.Release(); }
        foreach (ConsoleSessionFollowUpFailure failure in failures) PublishFailure(failure);
    }

    private static async Task DisposeAttachmentAsync(IConsoleSessionAttachment attachment,
        SessionTransitionDeadline deadline, AsyncCleanup cleanup)
    {
        await cleanup.RunTaskAsync(() => deadline.RunAsync(_ => attachment.DisposeSessionAsync().AsTask(), CancellationToken.None));
        await cleanup.RunTaskAsync(() => deadline.RunAsync(_ => attachment.DisposeManualControlsAsync().AsTask(), CancellationToken.None));
        await cleanup.RunTaskAsync(() => deadline.RunAsync(_ => attachment.DisposeOwnerAsync().AsTask(), CancellationToken.None));
    }

    public ValueTask DisposeAsync() => disposal.RunAsync(DisposeCoreAsync);

    private async Task DisposeCoreAsync()
    {
        Volatile.Write(ref disposalStarted, 1);
        lifetime.Cancel();
        var cleanup = new AsyncCleanup();
        using var deadline = new SessionTransitionDeadline(timeout);
        bool entered = false;
        IConsoleSessionAttachment early = active;
        early.StopAdmission();
        cleanup.Run(early.SuppressLiveOutput);
        try
        {
            await deadline.WaitAsync(transitionGate, CancellationToken.None).ConfigureAwait(false);
            entered = true;
            if (!ReferenceEquals(active, early))
            {
                active.StopAdmission();
                cleanup.Run(active.SuppressLiveOutput);
            }
            await cleanup.RunTaskAsync(() => deadline.RunAsync(
                token => active.ApplicationSession.QuiesceAsync(token).AsTask(), CancellationToken.None));
            cleanup.Run(closeAllPresentation);
            cleanup.Run(active.DetachBindings);
            await DisposeAttachmentAsync(active, deadline, cleanup);
            cleanup.ThrowIfFailed();
        }
        finally
        {
            if (entered) transitionGate.Release();
            lifetime.Dispose();
        }
    }

    private void PublishFailure(ConsoleSessionFollowUpFailure failure)
    {
        foreach (EventHandler<ConsoleSessionFollowUpFailure> observer in ReplacementFollowUpFailed?.GetInvocationList() ?? [])
        {
            try { observer(this, failure); }
            catch { /* Follow-up reporting cannot undo completed publication. */ }
        }
    }
}
