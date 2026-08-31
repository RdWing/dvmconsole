using Avalonia;
using Avalonia.Controls;
using DvmConsole.Presentation;

namespace DvmConsole.Desktop;

public sealed partial class OperatorToolsWindow
{
    private void MountSection(OperatorToolSection section)
    {
        if (mountedSection == section && toolContent.Content is Control)
            return;

        DetachHistoryViewport();
        generalSettingsView = null;
        historyView = null;
        connectionsSettingsView = null;

        Control content = CreateSectionContent(section);
        content.DataContext = viewModel;
        toolContent.Content = content;
        mountedSection = section;
        if (section == OperatorToolSection.History)
            ScheduleHistoryViewportHook();
    }

    private Control CreateSectionContent(OperatorToolSection section)
        => section switch
        {
            OperatorToolSection.General => CreateGeneralSettingsView(),
            OperatorToolSection.Audio => CreateAudioSettingsView(),
            OperatorToolSection.Tones => CreateToneSettingsView(),
            OperatorToolSection.Streams => CreateWebStreamsSettingsView(),
            OperatorToolSection.Recorder => CreateRecorderSettingsView(),
            OperatorToolSection.History => CreateHistoryView(),
            OperatorToolSection.Groups => CreateGroupSettingsView(),
            OperatorToolSection.Connections => CreateConnectionsSettingsView(),
            OperatorToolSection.Ptt => CreatePttSettingsView(),
            _ => throw new ArgumentOutOfRangeException(
                nameof(section),
                section,
                "The requested settings section does not have a direct page.")
        };

    private GeneralSettingsView CreateGeneralSettingsView()
    {
        var view = new GeneralSettingsView();
        view.ResetLayoutRequested += HandleSharedResetLayoutRequested;
        view.SaveToolbarClocksRequested += HandleSharedSaveToolbarClocksRequested;
        generalSettingsView = view;
        return view;
    }

    private ScrollViewer CreateAudioSettingsView()
    {
        var view = new AudioSettingsView { Margin = new Thickness(0, 0, 0, 20) };
        view.TestPermitToneRequested += HandleSharedTestPermitToneRequested;
        view.MicrophonePermissionRequested += HandleSharedMicrophonePermissionRequested;
        view.SavePresetRequested += HandleSharedSaveAudioInputPresetRequested;
        view.UsePresetRequested += HandleSharedUseAudioInputPresetRequested;
        view.DeletePresetRequested += HandleSharedDeleteAudioInputPresetRequested;
        view.SaveChannelRouteRequested += HandleSharedSaveChannelOutputRouteRequested;
        return new ScrollViewer { Margin = new Thickness(10), Content = view };
    }

    private ToneSettingsView CreateToneSettingsView()
    {
        var view = new ToneSettingsView();
        view.UseDtmfPresetRequested += HandleSharedUseDtmfPresetRequested;
        view.SendDtmfPresetRequested += HandleSharedSendDtmfPresetRequested;
        view.DeleteDtmfPresetRequested += HandleSharedDeleteDtmfPresetRequested;
        view.UseTonePresetRequested += HandleSharedUseTonePresetRequested;
        view.SendTonePresetRequested += HandleSharedSendTonePresetRequested;
        view.DeleteTonePresetRequested += HandleSharedDeleteTonePresetRequested;
        view.SendQuickCallRequested += HandleSharedSendQuickCallRequested;
        view.AddToneStepRequested += HandleSharedAddToneStepRequested;
        view.AddSilenceStepRequested += HandleSharedAddSilenceStepRequested;
        view.RemoveToneStepRequested += HandleSharedRemoveToneStepRequested;
        view.MoveToneStepUpRequested += HandleSharedMoveToneStepUpRequested;
        view.MoveToneStepDownRequested += HandleSharedMoveToneStepDownRequested;
        view.ImportAlertToneRequested += HandleSharedImportAlertToneRequested;
        view.SendAlertToneRequested += HandleSharedSendAlertToneRequested;
        view.DeleteAlertToneRequested += HandleSharedDeleteAlertToneRequested;
        return view;
    }

    private WebStreamsSettingsView CreateWebStreamsSettingsView()
    {
        var view = new WebStreamsSettingsView();
        view.SaveRouteRequested += HandleSharedSaveWebStreamOutputDeviceRequested;
        return view;
    }

    private RecorderSettingsView CreateRecorderSettingsView()
    {
        var view = new RecorderSettingsView();
        view.ChooseRecordingLocationRequested += HandleSharedChooseRecordingLocationRequested;
        view.ApplyRecordingLocationRequested += HandleSharedApplyRecordingLocationRequested;
        view.SaveIgnoredSubscribersRequested += HandleSharedSaveIgnoredSubscribersRequested;
        return view;
    }

    private CallHistoryView CreateHistoryView()
    {
        var view = new CallHistoryView();
        view.ExportRequested += HandleSharedHistoryExportRequested;
        view.ClearRequested += HandleSharedHistoryClearRequested;
        view.ClearFiltersRequested += HandleSharedHistoryClearFiltersRequested;
        view.PlayRequested += HandleSharedHistoryPlayRequested;
        view.StopRequested += HandleSharedHistoryStopRequested;
        view.OpenRequested += HandleSharedHistoryOpenRequested;
        view.DeleteRequested += HandleSharedHistoryDeleteRequested;
        historyView = view;
        return view;
    }

    private GroupSettingsView CreateGroupSettingsView()
    {
        var view = new GroupSettingsView();
        view.SaveGroupRequested += HandleSharedSavePatchGroupRequested;
        view.ToggleMultiSelectPttRequested += HandleSharedToggleMultiSelectPttRequested;
        return view;
    }

    private ConnectionsSettingsView CreateConnectionsSettingsView()
    {
        var view = new ConnectionsSettingsView();
        view.ToggleConnectionRequested += HandleSharedToggleSystemConnectionRequested;
        view.RestartConnectionRequested += HandleSharedRestartSystemRequested;
        connectionsSettingsView = view;
        return view;
    }

    private PttSettingsView CreatePttSettingsView()
    {
        var view = new PttSettingsView();
        view.ApplyGlobalKeyRequested += HandleSharedApplyGlobalPttKeyRequested;
        view.ApplyActiveSystemKeyRequested += HandleSharedApplyActiveSystemPttKeyRequested;
        view.KeyboardPermissionRequested += HandleSharedKeyboardPermissionRequested;
        view.RefreshSerialDevicesRequested += HandleSharedRefreshSerialPttDevicesRequested;
        view.ApplySerialSettingsRequested += HandleSharedApplySerialPttSettingsRequested;
        return view;
    }
}
