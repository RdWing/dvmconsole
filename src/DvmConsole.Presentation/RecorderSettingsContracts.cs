using Avalonia.Media;
using System.Collections;
using System.Windows.Input;

namespace DvmConsole.Presentation;

public interface IRecorderChannelViewModel
{
    string Name { get; }
    string DestinationText { get; }
    string RecordingConfigurationButtonText { get; }
    IBrush RecordingSelectionBrush { get; }
    IBrush RecordingSelectionBorderBrush { get; }
    ICommand RecordingCommand { get; }
    string IgnoredSubscriberIdsText { get; set; }
}

public interface IRecorderSystemViewModel
{
    string Name { get; }
    IEnumerable RecorderChannels { get; }
}

public interface IRecorderSettingsViewModel
{
    bool IsExternalRecordingLocationAvailable { get; }
    string RecordingLocationText { get; set; }
    string RecordingRetentionDaysText { get; set; }
    ICommand ApplyRecordingRetentionCommand { get; }
    IEnumerable RecorderSystems { get; }
}

public sealed class RecorderChannelEventArgs(IRecorderChannelViewModel channel) : EventArgs
{
    public IRecorderChannelViewModel Channel { get; } = channel ?? throw new ArgumentNullException(nameof(channel));
}
