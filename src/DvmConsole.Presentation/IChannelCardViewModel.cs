// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

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
    AudioMeterState AudioMeter { get; }
    double AudioMeterWidth { get; }
    double VolumeSliderValue { get; set; }
    string VolumeAutomationName { get; }
    string EncryptionButtonText { get; }
    string EncryptionAutomationName { get; }
    string EncryptionAutomationHelpText { get; }
    IBrush EncryptionSelectionBrush { get; }
    IBrush EncryptionSelectionBorderBrush { get; }
    IBrush EncryptionSelectionTextBrush { get; }
    ICommand EncryptionCommand { get; }
    bool CanToggleEncryption { get; }
    string PttButtonText { get; }
    string PttAutomationName { get; }
    string PttAutomationHelpText { get; }
    bool IsPttControlEnabled { get; }
    bool IsTransmitSelected { get; }
    string TransmitSelectionText { get; }
    string TransmitSelectionAutomationName { get; }
    IBrush TransmitSelectionBrush { get; }
    IBrush TransmitSelectionBorderBrush { get; }
    bool CanTransmit { get; }
    bool IsPageSelected { get; }
    string PageSelectionText { get; }
    string PageSelectionAutomationName { get; }
    IBrush PageSelectionBrush { get; }
    IBrush PageSelectionBorderBrush { get; }
    bool IsAlertSelected { get; }
    string AlertSelectionText { get; }
    string AlertSelectionAutomationName { get; }
    IBrush AlertSelectionBrush { get; }
    IBrush AlertSelectionBorderBrush { get; }
    bool IsRecordingEnabled { get; }
    string RecordButtonText { get; }
    string RecordingAutomationName { get; }
    string RecordingAutomationHelpText { get; }
    IBrush RecordingSelectionBrush { get; }
    IBrush RecordingSelectionBorderBrush { get; }
    ICommand RecordingCommand { get; }
    bool CanRecord { get; }
}
