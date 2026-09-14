// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.FneClient;

/// <summary>Explicit local qualification of the real transport; never contacts an external FNE.</summary>
public static class FneLoopbackDiagnostics
{
    public static async Task<string> RunAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        await using var master = new FneLoopbackMaster();
        Task serving = master.Completion;
        try
        {
            await using var connection = new FneConnection(master.CreateOptions());
            for (int cycle = 0; cycle < 2; cycle++)
            {
                var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Changed(object? sender, FneConnectionStatus status)
                {
                    if (status.State == FneConnectionState.Connected) connected.TrySetResult();
                }
                connection.StatusChanged += Changed;
                try
                {
                    await connection.StartAsync(deadline.Token);
                    Task completed = await Task.WhenAny(connected.Task, serving).WaitAsync(deadline.Token);
                    await completed;
                    if (completed == serving) throw new InvalidOperationException("Loopback master stopped before connection completed.");
                    await connection.StopAsync(deadline.Token);
                    if (connection.Status.State != FneConnectionState.Disconnected)
                        throw new InvalidOperationException("Transport did not return to Disconnected.");
                }
                finally { connection.StatusChanged -= Changed; }
            }
            if (master.Authentications < 2)
                throw new InvalidOperationException("Reconnect did not authenticate a new peer session.");
            return "PASS\nReal loopback UDP transport, salted authentication, configuration handshake, stop and reconnect through FneConnection. No external server, RF traffic or microphone.";
        }
        finally { deadline.Cancel(); }
    }
}
