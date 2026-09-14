// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

namespace DvmConsole.Desktop;

/// <summary>Desktop file destination adapter for the shared diagnostic workspace.</summary>
internal sealed class DebugLogWorkspace(
    Func<bool> hasUiThreadAccess,
    Action<Action> postToUiThread,
    Func<bool> isStopped,
    TimeSpan? filterDebounceInterval = null,
    Func<TimeSpan, CancellationToken, Task>? debounceDelayAsync = null)
    : DvmConsole.Presentation.DebugLogWorkspace(hasUiThreadAccess, postToUiThread, isStopped,
        filterDebounceInterval, debounceDelayAsync)
{
    public int Export(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        using var destination = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        return Export(destination);
    }

}
