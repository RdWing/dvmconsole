// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Audio;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Runtime;
using DvmConsole.Core.Settings;
using DvmConsole.Ptt;

namespace DvmConsole.Desktop;

/// <summary>
/// Constructs session-scoped presentation controllers at the desktop
/// composition boundary. Controllers receive their narrow ports here while
/// <see cref="MainWindowViewModel"/> remains a binding facade.
/// </summary>
internal sealed class MainWindowSessionComposition
{
    public static MainWindowSessionComposition Default { get; } = new();

    public HistoryRecordingController CreateHistoryRecording(
        string recordingRetentionDays,
        string recordingRootPath, ConsoleCallHistory? history = null)
        => new(recordingRetentionDays, recordingRootPath, history);

    public HistoryDiagnosticsController CreateHistoryDiagnostics(
        HistoryRecordingController history,
        DebugLogWorkspace debugLogs,
        IHistoryDiagnosticsSession session,
        ConsoleSubscriberCommandDispatcher? subscriberCommands = null)
        => new(history, debugLogs, session, subscriberCommands);

    public RecordingCommandController CreateRecordingCommands(IRecordingCommandSession session)
        => new(session);

    public OperatorUndoController CreateOperatorUndo(
        Action<Action> dispatch,
        Action<Exception> reportFailure)
        => new(dispatch, reportFailure);

    public TonePresentationController CreateTonePresentation(
        ToneWorkspaceViewModel workspace,
        UserSettings settings,
        ITonePresentationSession session)
        => new(workspace, settings, session);

    public ShellLayoutController CreateShellLayout(
        UserSettings settings,
        IReadOnlyList<ZoneViewModel> zones,
        IShellLayoutSessionPort session)
        => new(settings, zones, session);

    public ShellSettingsController CreateShellSettings(
        UserSettingsStore store,
        UserSettings settings,
        IShellSettingsSessionPort session,
        IUiDispatcher? dispatcher = null)
        => new(store, settings, session, dispatcher);

    public PttSessionController CreatePttSession(
        PttSettingsViewModel settings,
        Func<string, int, IPttInputSourceFactory> serialFactory,
        Func<PttTargetScope> getSerialTargetScope)
        => new(settings, serialFactory, getSerialTargetScope);

    public WebStreamPlaybackCoordinator CreateWebStreamPlayback(
        ConsoleOperationalRuntime runtime, ConsoleWebPlaybackDependencies dependencies,
        Func<WebStreamViewModel, string?>? getStreamOutputDeviceId, IUiDispatcher uiDispatcher)
        => new(observer => runtime.InitializeWebPlayback(dependencies, observer),
            getStreamOutputDeviceId, uiDispatcher);

    public WebStreamOperatorController CreateWebStreamOperator(
        UserSettings settings,
        string configurationIdentity,
        WebStreamPlaybackCoordinator playback,
        IWebStreamOperatorSession session)
        => new(settings, configurationIdentity, playback, session);

    public RuntimeHealthController CreateRuntimeHealth(
        IRecordingFinalizationHealthSource recordingFinalization)
        => new(recordingFinalization);

    public AudioInputSettingsController CreateAudioInputSettings(
        AudioSettingsViewModel workspace,
        UserSettings settings,
        IAudioInputSettingsSession session)
        => new(workspace, settings, session);

    public PatchRoutingController CreatePatchRouting(
        PatchForwardingCoordinator forwarding,
        IEnumerable<ChannelViewModel> channels,
        IEnumerable<GroupConfiguration> groupDefinitions,
        bool retainOnStartup,
        IPatchRoutingSessionPort session,
        PatchConfigurationRuntime? preparedConfiguration = null)
        => new(forwarding, channels, groupDefinitions, retainOnStartup, session, preparedConfiguration);


}
