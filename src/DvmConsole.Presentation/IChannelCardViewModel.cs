using System.Windows.Input;
using Avalonia.Media;

namespace DvmConsole.Presentation;

// Typed contract for the unchanged desktop card renderer. The desktop adapter
// may implement it from legacy state while future hosts project the same view
// from application snapshots. Keeping the contract here allows compiled XAML
// bindings and trim-safe publication without a Desktop reference.
public interface IChannelCardViewModel
{
    string Name { get; }
    string TalkgroupText { get; }
    string LastCallerDisplayText { get; }
    IBrush CardBackgroundBrush { get; }
    IBrush CardBorderBrush { get; }
    IBrush CardTextBrush { get; }
    double CardWidth { get; }
    double AudioMeterWidth { get; }
    double AudioFillWidth { get; }
    double AudioPeakMarkerX { get; }
    IBrush AudioPeakMarkerBrush { get; }
    bool IsAudioPeakVisible { get; }
    double VolumeSliderValue { get; set; }
    string EncryptionButtonText { get; }
    IBrush EncryptionSelectionBrush { get; }
    IBrush EncryptionSelectionBorderBrush { get; }
    IBrush EncryptionSelectionTextBrush { get; }
    ICommand EncryptionCommand { get; }
    bool CanToggleEncryption { get; }
    string PttButtonText { get; }
    bool IsPttControlEnabled { get; }
    bool IsTransmitSelected { get; }
    string TransmitSelectionText { get; }
    IBrush TransmitSelectionBrush { get; }
    IBrush TransmitSelectionBorderBrush { get; }
    bool CanTransmit { get; }
    bool IsPageSelected { get; }
    string PageSelectionText { get; }
    IBrush PageSelectionBrush { get; }
    IBrush PageSelectionBorderBrush { get; }
    bool IsAlertSelected { get; }
    string AlertSelectionText { get; }
    IBrush AlertSelectionBrush { get; }
    IBrush AlertSelectionBorderBrush { get; }
    bool IsRecordingEnabled { get; }
    string RecordButtonText { get; }
    IBrush RecordingSelectionBrush { get; }
    IBrush RecordingSelectionBorderBrush { get; }
    ICommand RecordingCommand { get; }
    bool CanRecord { get; }
}
