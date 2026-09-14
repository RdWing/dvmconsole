// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

internal sealed partial class ConsoleOperationalRuntime
{
    public ConsoleRadioLifecycleController RadioLifecycle { get; private set; } = null!;

    public void InitializeRadioLifecycle(ConsoleLiveRadioLifecyclePorts ports)
    {
        if (RadioLifecycle is not null) throw new InvalidOperationException("Radio lifecycle is already initialized.");
        RadioLifecycle = new(this, ports.State, ports.Radios, ports.Keys, ports.KeyRequests,
            ports.Subscribers, ports.Host);
    }

    public void AttachRadioIngress(ConsoleLiveIngressPorts ports)
    {
        if (!IsComposed) throw new InvalidOperationException("Complete live composition before attaching radio ingress.");
        InitializeRadioIngress(ports.Scope, ports.Name, ingress =>
        {
            ingress.TrafficReceived -= ports.Traffic;
            ingress.AuthorityChanged -= ports.Authority;
            ingress.ConnectionChanged -= ports.Connection;
            ingress.P25KeyReceived -= ports.Key;
            ingress.SubscriberAcknowledged -= ports.Subscriber;
            ingress.LogPublished -= ports.Log;
        });
        RadioIngress.TrafficReceived += ports.Traffic;
        RadioIngress.AuthorityChanged += ports.Authority;
        RadioIngress.ConnectionChanged += ports.Connection;
        RadioIngress.P25KeyReceived += ports.Key;
        RadioIngress.SubscriberAcknowledged += ports.Subscriber;
        RadioIngress.LogPublished += ports.Log;
    }
}
