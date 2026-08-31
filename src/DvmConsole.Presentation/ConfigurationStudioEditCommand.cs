using DvmConsole.Core.Configuration;

namespace DvmConsole.Presentation;

public enum ConfigurationStudioEditCommand
{
    AddChannel,
    DuplicateChannel,
    DeleteChannel,
    MoveChannelUp,
    MoveChannelDown,
    ApplySelectedCardSize,
    SetSelectedRowsRxOnly,
    SetSelectedRowsTxCapable,
    AddZone,
    DuplicateZone,
    DeleteZone
}

public sealed class ConfigurationStudioEditCommandEventArgs(
    ConfigurationStudioEditCommand command,
    IReadOnlyList<ChannelConfiguration> selectedChannels) : EventArgs
{
    public ConfigurationStudioEditCommand Command { get; } = command;
    public IReadOnlyList<ChannelConfiguration> SelectedChannels { get; } = selectedChannels;
}
