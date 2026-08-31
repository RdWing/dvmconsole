using DvmConsole.Application;
using System.ComponentModel;

namespace DvmConsole.Presentation;

/// <summary>
/// Minimal channel projection required by shared patch and multi-select
/// editors. Runtime routing remains ID-based and owned by Application.
/// </summary>
public interface IPatchMemberChannelViewModel : INotifyPropertyChanged
{
    ChannelId Id { get; }
    string RoutingKey { get; }
    string SettingsKey { get; }
    string SystemName { get; }
    string Name { get; }
    string ModeText { get; }
    uint DestinationId { get; }
    bool CanListen { get; }
    bool CanTransmit { get; }
}
