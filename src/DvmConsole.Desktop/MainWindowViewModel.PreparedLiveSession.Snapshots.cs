// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel
{
    internal sealed partial class PreparedLiveSession
    {
        public ConsoleSnapshotState Snapshots => Runtime.Snapshots;

        private ConsoleLiveSnapshotPorts CreateSnapshotPorts()
            => new(State!.Topology, new ConsoleSnapshotContextSource(Recordings,
                (id, muted) => Runtime.Receive.Output.GetEffectiveMuteReason(id, muted),
                () => State.ReceiveMute.GloballyMuted, () => PlaybackState.Snapshot.Channel,
                () => PatchConfiguration.MembershipIndex), State.Status, State.ReceiveMute, Recordings);
    }
}
