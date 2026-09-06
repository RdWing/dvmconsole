// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel : IRecorderSettingsViewModel
{
    public bool IsExternalRecordingLocationAvailable => true;
    public bool IsRecordingUnavailable => !callRecordings.CanWriteRecordings;
    public string? RecordingAvailabilityWarning => IsRecordingUnavailable
        ? callRecordings.FinalizationHealth.LastError : null;

    public string RecordingLocationText
    {
        get => RecordingRootPathText;
        set => RecordingRootPathText = value;
    }

    System.Collections.IEnumerable IRecorderSettingsViewModel.RecorderSystems => Systems;
}
