// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

public sealed partial class ConsoleReceiveSession : IConsoleRadioLifecyclePort
{
    private ConsoleRadioLifecycleController radioLifecycle => operationalRuntime.RadioLifecycle;

    private ConsoleLiveRadioLifecyclePorts PrepareRadioLifecycle()
    {
        var requests = radios.Sessions.Values.Where(radio => radio is IRadioP25KeyEndpoint).ToDictionary(radio => radio.SystemId,
                radio => new P25KeyRequestPort(state.KeyRequests[radio.SystemId],
                    ((IRadioP25KeyEndpoint)radio).RequestP25Key));
        return new(state, radios.Sessions, p25KeyRetrieval, requests, subscriberCommands, this);
    }

    bool IConsoleRadioLifecyclePort.IsStopping => IsStopping;
    void IConsoleRadioLifecyclePort.ChannelChanged(ChannelId channel) => Changed(channel);
    void IConsoleRadioLifecyclePort.PublishStatus(string message) => SetStatus(message);

    void IConsoleRadioLifecyclePort.AuthorityChanged(TalkgroupAuthorityRecord record,
        IReadOnlyList<ChannelId> unavailable, int stoppedPatchTargets)
    {
        if (manualOwner is { } owner && owner.Targets.Any(unavailable.Contains))
            RequestManualRelease();
        SetStatus($"{radios.Sessions[record.SystemId].Name}: FNE talkgroup table changed; unavailable transmit targets stopped.");
    }

    void IConsoleRadioLifecyclePort.ConnectionChanged(IRadioSession radio,
        RadioConnectionSnapshot snapshot, ConsoleConnectionChange transition)
    {
        connectionCues?.Observe(snapshot.Name, snapshot.State);
        foreach (EventHandler observer in ConnectionStatesChanged?.GetInvocationList() ?? [])
        {
            try { observer(this, EventArgs.Empty); }
            catch { /* Connection progress must survive a detached presentation. */ }
        }
    }

    void IConsoleRadioLifecyclePort.KeyStateChanged(IRadioSession radio)
    {
        lock (ingressSync) Changed();
    }

    void IConsoleRadioLifecyclePort.KeyReceived(IRadioSession radio, RadioP25KeyResponse response,
        bool accepted, string message)
    {
        if (accepted) { lock (ingressSync) Changed(); }
        SetStatus(message);
    }
}
