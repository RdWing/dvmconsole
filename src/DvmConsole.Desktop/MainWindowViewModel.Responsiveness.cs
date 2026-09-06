// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Diagnostics;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel
{
    private readonly UiLatencyReporter uiLatencyReporter;

    private void PublishUiLatency(UiLatencyObservation observation)
        => AddDebugLog(DateTimeOffset.Now, "UI", DebugLogSeverity.Warning,
            $"{observation.Category} UI update ({observation.Context}): queue wait {observation.QueueDelay.TotalMilliseconds:0} ms, " +
            $"apply {observation.ApplyDuration.TotalMilliseconds:0} ms (render timing not measured).");

    private void HandleChannelSelectionChanged(ChannelViewModel channel, ChannelSelectionChange change)
        => AddDebugLog(DateTimeOffset.Now, "Selection", DebugLogSeverity.Info,
            $"{(change.Kind == ChannelSelectionKind.Receive ? "RX" : "TAR")} selection on " +
            $"{channel.SystemName}/{channel.Name}: {change.Previous} -> {change.Current}; origin: {change.Origin}.");
}
