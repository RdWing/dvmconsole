using Avalonia.Media;
using System.Collections;

namespace DvmConsole.Presentation;

public interface IToolbarClockUtcOffsetOption
{
    string Label { get; }
}

public interface IToolbarClockColorOption
{
    string Label { get; }
    IBrush ColorBrush { get; }
}

public interface IToolbarClockViewModel
{
    bool Enabled { get; set; }
    string SlotLabel { get; }
    IEnumerable UtcOffsetOptions { get; }
    IToolbarClockUtcOffsetOption? SelectedUtcOffsetOption { get; set; }
    IEnumerable ColorOptions { get; }
    IToolbarClockColorOption SelectedColorOption { get; set; }
    string TimeZoneLabel { get; }
}

public interface IGeneralSettingsViewModel
{
    string SettingsVersionText { get; }
    string UiFontSizeText { get; }
    double UiFontSize { get; set; }
    string UiScaleText { get; }
    double UiScale { get; set; }
    bool TogglePttMode { get; set; }
    bool TalkPermitTone { get; set; }
    bool ConnectionChimes { get; set; }
    bool LocalToneMonitorEnabled { get; set; }
    bool VerboseLoggingEnabled { get; set; }
    bool MuteRxAudioWhileTransmitting { get; set; }
    bool RestoreSelectedChannelsOnStartup { get; set; }
    bool RetainPatchStateOnStartup { get; set; }
    bool DarkMode { get; set; }
    bool KeepWindowOnTop { get; set; }
    bool ShowSystemStatus { get; set; }
    bool ShowChannels { get; set; }
    bool ShowAlertTones { get; set; }
    bool LockWidgets { get; set; }
    IEnumerable ToolbarClocks { get; }
    bool ClockUse24HourTime { get; set; }
    bool ClockShowSeconds { get; set; }
    string GlobalPttKeyText { get; }
    string ActiveSystemPttKeyText { get; }
}
