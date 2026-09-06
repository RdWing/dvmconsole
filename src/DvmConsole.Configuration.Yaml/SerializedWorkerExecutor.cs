// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Configuration.Yaml;

/// <summary>
/// Serializes storage transactions and starts every transaction on the default
/// scheduler so synchronous parsing and file work never inherit a UI context.
/// </summary>
internal sealed class SerializedWorkerExecutor
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Func<IDisposable>? acquireCrossProcessLock;

    public SerializedWorkerExecutor(Func<IDisposable>? acquireCrossProcessLock = null)
        => this.acquireCrossProcessLock = acquireCrossProcessLock;

    public Task RunAsync(
        Action<CancellationToken> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return RunAsync(
            token =>
            {
                operation(token);
                return Task.CompletedTask;
            },
            cancellationToken);
    }

    public Task<T> RunAsync<T>(
        Func<CancellationToken, T> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return RunAsync(
            token => Task.FromResult(operation(token)),
            cancellationToken);
    }

    public Task RunAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return RunAsync(
            async token =>
            {
                await operation(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    public Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return Task.Run(
            async () =>
            {
                bool entered = false;
                try
                {
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    entered = true;
                    cancellationToken.ThrowIfCancellationRequested();
                    using IDisposable? processLock = acquireCrossProcessLock?.Invoke();
                    cancellationToken.ThrowIfCancellationRequested();
                    return await operation(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    if (entered)
                        gate.Release();
                }
            },
            CancellationToken.None);
    }
}
