// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

internal interface IManualTransmitStartContext
{
    bool IsInputSuppressed { get; }
    bool NetworkDisabled { get; }
    bool HasActiveTransmission { get; }
    bool PlayPermitTone { get; }
    TransmitChannelDescriptor CaptureChannel(ChannelId id);
    IRadioTrafficEndpoint? ResolveSystem(string name);
    Task SetStatusAsync(string status);
    Task SetStartingAsync(IReadOnlyList<ChannelId> channels);
}

/// <summary>Owns manual start/release admission and target selection for the shared TX lifecycle.</summary>
internal sealed class ManualTransmitCoordinator(
    SemaphoreSlim admission,
    IManualTransmitStartContext context,
    Func<TransmitStartRequest, Task> start,
    Func<IReadOnlyList<ChannelId>, string?, bool, Task> stop)
{
    private readonly object sync = new();
    private CancellationTokenSource? startup;
    private ChannelId[] startupChannels = [];
    private long generation;

    public async Task StopAsync(IReadOnlyList<ChannelId> channelIds,
        string? stoppedStatusText = null, bool propagateUnconfirmedStop = false)
    {
        CancelStartup();
        // Unlike a new press, release must wait for an admitted startup or tone
        // operation. Dropping this edge could leave a transmitter active.
        await admission.WaitAsync().ConfigureAwait(false);
        try { await stop(channelIds, stoppedStatusText, propagateUnconfirmedStop).ConfigureAwait(false); }
        finally { admission.Release(); }
    }

    public void CancelStartup()
    {
        // Revoke a pending microphone startup before waiting for its cleanup.
        // Cancellation and disposal share the lock so release cannot cancel a
        // retired source or accidentally revoke a later press.
        lock (sync)
        {
            generation++;
            startup?.Cancel();
        }
    }

    public bool CancelChannelStartup(ChannelId channel)
    {
        lock (sync)
        {
            if (startup is null || !startupChannels.Contains(channel)) return false;
            generation++;
            startup.Cancel();
            return true;
        }
    }

    public async Task StartAsync(IReadOnlyList<ChannelId> channelIds, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long requestedGeneration;
        lock (sync) requestedGeneration = generation;
        // A held tone or startup drops this edge; it must never queue a later TX.
        if (!await admission.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            await context.SetStatusAsync("PTT unavailable while another transmit operation is in progress.")
                .ConfigureAwait(false);
            return;
        }
        try
        {
            CancellationToken startupToken;
            lock (sync)
            {
                if (requestedGeneration != generation) return;
                startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                startupChannels = channelIds.ToArray();
                startupToken = startup.Token;
            }
            startupToken.ThrowIfCancellationRequested();
            if (context.IsInputSuppressed) return;
            if (context.NetworkDisabled)
            {
                await context.SetStatusAsync("Demo safety boundary: PTT input observed; network output remains disabled.")
                    .ConfigureAwait(false);
                return;
            }
            if (channelIds.Count == 0 || context.HasActiveTransmission) return;
            TransmitChannelDescriptor[] channels = channelIds.Select(context.CaptureChannel).ToArray();
            var receiving = channels.FirstOrDefault(channel => channel.ReceiveActive && !channel.AllowsTransmitDuringReceive);
            if (receiving is not null)
            {
                await context.SetStatusAsync($"PTT unavailable: {receiving.Name} is currently receiving.").ConfigureAwait(false);
                return;
            }
            var targets = new List<TransmitTarget>(channels.Length);
            foreach (var channel in channels)
            {
                IRadioTrafficEndpoint? system = context.ResolveSystem(channel.Definition.SystemName);
                if (system is null)
                {
                    await context.SetStatusAsync($"PTT unavailable: system '{channel.Definition.SystemName}' was not found.")
                        .ConfigureAwait(false);
                    return;
                }
                targets.Add(new(channel, system));
            }
            startupToken.ThrowIfCancellationRequested();
            await context.SetStartingAsync(channelIds).ConfigureAwait(false);
            // The lifecycle receives revoked intent too, so its failure path
            // clears the starting presentation before returning.
            await start(new(targets, context.PlayPermitTone, startupToken)).ConfigureAwait(false);
        }
        finally
        {
            lock (sync) { startup?.Dispose(); startup = null; startupChannels = []; }
            admission.Release();
        }
    }
}
