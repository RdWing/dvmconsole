// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Diagnostics;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Operations;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel : IReceiveChannelTrafficPort, IReceiveCallHistoryPresentation
{
    void IReceiveChannelTrafficPort.Projected(ChannelId channel, ChannelReceiveProjectionResult result)
        => ResolveChannel(channel).PresentReceiveProjection(result);
    void IReceiveChannelTrafficPort.IgnoredLate(ChannelId channel, uint stream, DateTimeOffset now)
        => PostToUi(() => PublishReceiveDiagnostics(ResolveChannel(channel), stream, now));
    void IReceiveChannelTrafficPort.HistoryChanged() => PostToUi(NotifyCallHistoryChanged);
    void IReceiveChannelTrafficPort.NonCallTerminator(SystemId system)
        => Systems.First(view => SystemId.FromName(view.Name) == system).RecordNonCallDmrTerminator();
    void IReceiveChannelTrafficPort.Log(DateTimeOffset timestamp, string source, DebugLogSeverity severity, string message)
        => PostToUi(() => AddDebugLog(timestamp, source, severity, message));

    string IReceiveCallHistoryPresentation.DescribeSignalQuality(DvmConsole.Core.Runtime.IRadioMediaFrame traffic)
        => DescribeFneSignalQuality((FneTrafficFrame)traffic);
    void IReceiveCallHistoryPresentation.Project(ConsoleCallHistoryRecord record)
        => PostToUi(() =>
        {
            if (callHistory.Runtime.Find(record.Id) is { } current) callHistory.ProjectRuntimeRecord(current);
        });
    void IReceiveCallHistoryPresentation.Log(DateTimeOffset timestamp, string source, DebugLogSeverity severity, string message)
        => PostToUi(() => AddDebugLog(timestamp, source, severity, message));
}
