// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Core.Settings;

namespace DvmConsole.Desktop;

internal sealed class ReceiveOutputPresentationPort(
    UserSettings settings,
    IReceiveRecordingSink recordings,
    ConsoleChannelMediaDirectory channels,
    Action<ChannelId, bool, bool> receiveSelectionChanged,
    Func<Action, Task> runOnUiThread,
    Action persistSettings,
    Action notifyMutePresentation,
    Action<string> publishStatus) : IReceiveOutputView
{
    public void ReceiveSelectionChanged(ChannelId channelId, bool previous, bool enabled)
        => receiveSelectionChanged(channelId, previous, enabled);

    public Task RunAsync(Action action) => runOnUiThread(action);

    public void SetSelectionPreference(ChannelId channelId, bool enabled)
    {
        string settingsKey = channels.State(channelId).SettingsKey;
        HashSet<string> selected = settings.ReceiveEnabledChannelKeys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool changed = enabled
            ? selected.Add(settingsKey)
            : selected.Remove(settingsKey);
        if (!changed)
            return;

        settings.ReceiveEnabledChannelKeys = selected
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        persistSettings();
    }

    public void StopRecording(ChannelId channelId) => recordings.StopChannel(channels.DescribeRecording(channelId));

    public void NotifyMuteChanged() => notifyMutePresentation();

    public void PublishStatus(string text) => publishStatus(text);
}

internal sealed class ReceiveOutputLifetimePort(
    Func<bool> isDisposing,
    Action<TimeSpan, string> observeRecovery) : IReceiveOutputLifetimePort
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public long GetTimestamp() => System.Diagnostics.Stopwatch.GetTimestamp();
    public TimeSpan GetElapsedTime(long started) => System.Diagnostics.Stopwatch.GetElapsedTime(started);
    public bool IsDisposing => isDisposing();

    public void ObserveRecovery(TimeSpan elapsed, string result)
        => observeRecovery(elapsed, result);
}
