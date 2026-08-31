using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DvmConsole.Presentation;

public sealed partial class AudioSettingsView : UserControl
{
    public AudioSettingsView()
    {
        InitializeComponent();
    }

    public event EventHandler? TestPermitToneRequested;
    public event EventHandler? MicrophonePermissionRequested;
    public event EventHandler? SavePresetRequested;
    public event EventHandler<AudioInputPresetEventArgs>? UsePresetRequested;
    public event EventHandler<AudioInputPresetEventArgs>? DeletePresetRequested;
    public event EventHandler<ChannelAudioRouteEventArgs>? SaveChannelRouteRequested;

    private void HandleTestPermitToneClick(object? sender, RoutedEventArgs e)
        => TestPermitToneRequested?.Invoke(this, EventArgs.Empty);
    private void HandleRequestMicrophonePermissionClick(object? sender, RoutedEventArgs e)
        => MicrophonePermissionRequested?.Invoke(this, EventArgs.Empty);
    private void HandleSavePresetClick(object? sender, RoutedEventArgs e)
        => SavePresetRequested?.Invoke(this, EventArgs.Empty);
    private void HandleUsePresetClick(object? sender, RoutedEventArgs e)
        => PublishPreset(sender, UsePresetRequested);
    private void HandleDeletePresetClick(object? sender, RoutedEventArgs e)
        => PublishPreset(sender, DeletePresetRequested);
    private void HandleSaveChannelRouteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: IChannelAudioRouteViewModel channel })
            SaveChannelRouteRequested?.Invoke(this, new ChannelAudioRouteEventArgs(channel));
    }

    private void PublishPreset(
        object? sender,
        EventHandler<AudioInputPresetEventArgs>? handler)
    {
        if (sender is Button { Tag: IAudioInputPresetViewModel preset })
            handler?.Invoke(this, new AudioInputPresetEventArgs(preset));
    }
}
