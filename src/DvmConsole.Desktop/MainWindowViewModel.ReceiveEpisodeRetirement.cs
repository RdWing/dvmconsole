// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Diagnostics;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel : IReceiveEpisodeRetirementPort
{
    void IReceiveEpisodeRetirementPort.ProjectCompletion(ConsoleCallCompletion completion)
        => PostToUi(() => callHistory.ProjectCompletion(completion));

    internal static bool IsEpisodePhysicallyActive(
        IReadOnlyList<SystemViewModel> systems, ReceiveCallEpisodeSnapshot episode)
        => ReceiveEpisodeTargetIndex.IsPhysicallyActive<SystemViewModel, ChannelViewModel>(
            systems, episode, static system => system.Name, static system => system.Channels,
            static channel => channel.SessionState);

    void IReceiveEpisodeRetirementPort.ReportCompleted(DateTimeOffset now, string systemName, string message)
        => PostToUi(() => AddDebugLog(now, systemName, DebugLogSeverity.Info, message));
    void IReceiveEpisodeRetirementPort.ReportFailure(ReceiveCallEpisodeSnapshot episode, Exception exception)
        => ReportReceiveEpisodeCompletionFailure(episode, exception);
}
