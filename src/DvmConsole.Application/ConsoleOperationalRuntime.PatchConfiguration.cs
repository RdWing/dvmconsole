// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Application;

internal sealed partial class ConsoleOperationalRuntime
{
    public ConsoleSnapshotState Snapshots => snapshots
        ?? throw new InvalidOperationException("Snapshots are not initialized.");

    public PatchConfigurationRuntime? PatchConfiguration { get; private set; }

    public void InitializePatchConfiguration(ConsoleLivePatchConfigurationPorts ports)
    {
        if (PatchConfiguration is not null)
            throw new InvalidOperationException("Patch configuration is already initialized.");
        PatchConfiguration = new(ports.Port ?? new PatchConfigurationPort(ports.Settings,
            TransmitChannels, Patches.Forwarding), ports.Definitions);
        if (ports.ApplyRestoration) PatchConfiguration.Restore(ports.RestoreEnabled);
    }

    public void ObservePatchSnapshotContext(ConsoleSnapshotState snapshots)
    {
        if (PatchConfiguration is not { } configuration) return;
        EventHandler changed = (_, _) => snapshots.NotifyContextChanged();
        services.Presentation.Register("patch-snapshot-context", () =>
        {
            configuration.Changed -= changed;
            return ValueTask.CompletedTask;
        });
        configuration.Changed += changed;
    }
}
