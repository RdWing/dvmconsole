using Avalonia.Controls;
using DvmConsole.Core.Configuration;

namespace DvmConsole.Desktop;

public sealed partial class MainWindow : Window
{
    public MainWindow() : this(null)
    {
    }

    public MainWindow(string? configurationPath)
    {
        InitializeComponent();
        DataContext = MainWindowViewModel.Load(configurationPath);
    }

    private void InitializeComponent()
    {
        Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
    }
}

public sealed class MainWindowViewModel
{
    private MainWindowViewModel(string statusText, IEnumerable<SystemViewModel> systems, IEnumerable<ZoneViewModel> zones)
    {
        StatusText = statusText;
        Systems = systems.ToArray();
        Zones = zones.ToArray();
    }

    public string StatusText { get; }
    public IReadOnlyList<SystemViewModel> Systems { get; }
    public IReadOnlyList<ZoneViewModel> Zones { get; }

    public static MainWindowViewModel Load(string? configurationPath)
    {
        if (string.IsNullOrWhiteSpace(configurationPath))
        {
            return new MainWindowViewModel(
                "No codeplug selected. Launch with a path to a codeplug YAML file.",
                [],
                []);
        }

        try
        {
            ConsoleConfiguration configuration = ConfigurationLoader.Load(configurationPath);
            IReadOnlyList<string> errors = ConfigurationLoader.Validate(configuration);
            string status = errors.Count == 0
                ? $"Loaded {configuration.Systems.Count} system(s) and {configuration.Zones.Count} zone(s). FNE connection services are not enabled yet."
                : $"Configuration has {errors.Count} validation error(s).";

            return new MainWindowViewModel(
                status,
                configuration.Systems.Select(system => new SystemViewModel(system.Name, $"{system.Address}:{system.Port}")),
                configuration.Zones.Select(zone => new ZoneViewModel(
                    zone.Name,
                    zone.Channels.Select(channel => new ChannelViewModel(
                        channel.Name,
                        channel.Mode.ToUpperInvariant(),
                        $"{channel.System} / TGID {channel.Tgid}")).ToArray())));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or YamlDotNet.Core.YamlException)
        {
            return new MainWindowViewModel($"Unable to load codeplug: {exception.Message}", [], []);
        }
    }
}

public sealed record SystemViewModel(string Name, string Endpoint);

public sealed record ZoneViewModel(string Name, IReadOnlyList<ChannelViewModel> Channels);

public sealed record ChannelViewModel(string Name, string ModeText, string DestinationText);
