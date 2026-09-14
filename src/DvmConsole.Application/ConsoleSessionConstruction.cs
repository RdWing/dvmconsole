// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

public static class ConsoleSessionConstruction
{
    /// <summary>
    /// Transfers registered services to the constructed session only on success.
    /// Cancellation after a late factory completion still retires those services.
    /// </summary>
    public static ValueTask<T> CreateAsync<T>(
        ConsoleSessionServices services,
        Func<CancellationToken, ValueTask<T>> construct,
        CancellationToken cancellationToken = default)
        => CreateCoreAsync(services, construct, null, cancellationToken);

    internal static ValueTask<T> CreateAsync<T>(ConsoleSessionServices services,
        SessionTerminalFence terminal, Func<CancellationToken, ValueTask<T>> construct,
        CancellationToken cancellationToken = default)
        => CreateCoreAsync(services, construct, terminal, cancellationToken);

    private static async ValueTask<T> CreateCoreAsync<T>(ConsoleSessionServices services,
        Func<CancellationToken, ValueTask<T>> construct, SessionTerminalFence? terminal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(construct);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            T result = await construct(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception constructionException)
        {
            terminal?.TryClose();
            await RollbackAsync(constructionException, services.DisposeAsync).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Retires a partially prepared host without losing its original failure.</summary>
    public static async ValueTask RollbackAsync(Exception failure, Func<ValueTask> rollback)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(rollback);
        var cleanup = new AsyncCleanup();
        cleanup.Capture(failure);
        // Caller cancellation must not skip resources already acquired.
        await cleanup.RunTaskAsync(() => rollback().AsTask()).ConfigureAwait(false);
        cleanup.ThrowIfFailed();
    }

    // Compatibility bridge for the existing synchronous desktop composition.
    // Both hosts share the asynchronous construction and rollback owner.
    public static T Create<T>(ConsoleSessionServices services, Func<T> construct)
    {
        ArgumentNullException.ThrowIfNull(construct);
        return CreateAsync(services, _ => ValueTask.FromResult(construct()))
            .AsTask().GetAwaiter().GetResult();
    }
}
