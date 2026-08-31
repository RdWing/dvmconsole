using System.Collections;

namespace DvmConsole.Presentation;

public interface IRxJitterBufferOptionViewModel
{
    string Label { get; }
}

public interface IRxJitterBufferModeViewModel
{
    string ModeName { get; }
    IEnumerable Options { get; }
    IRxJitterBufferOptionViewModel SelectedOption { get; set; }
}

public interface IConnectionSystemViewModel
{
    string Name { get; }
    string Endpoint { get; }
    IEnumerable RxJitterBufferModes { get; }
    string ConnectionButtonText { get; }
    string AdaptiveJitterLearnedText { get; }
    string JitterBufferEffectivenessText { get; }
    string ConnectionStatus { get; }
    string TrafficTotalsText { get; }
    string StreamTrafficText { get; }
    string ConnectionHealthText { get; }
}

public interface IKeyStatusItemViewModel
{
    string SystemName { get; }
    string ChannelName { get; }
    string ModeText { get; }
    string AlgorithmIdText { get; }
    string KeyIdText { get; }
    string StatusText { get; }
    string ConfigurationHint { get; }
    bool HasConfigurationHint { get; }
}

public interface IConnectionsSettingsViewModel
{
    IEnumerable ConnectionSystems { get; }
    IEnumerable KeyStatusItems { get; }
}

public sealed class ConnectionSystemEventArgs(IConnectionSystemViewModel system) : EventArgs
{
    public IConnectionSystemViewModel System { get; } = system ?? throw new ArgumentNullException(nameof(system));
}
