using Avalonia.Media;
using DvmConsole.Audio;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using DvmConsole.FneClient;
using DvmConsole.Media;

namespace DvmConsole.Desktop;

internal sealed record DesktopRuntimeDependencies(
    UserSettingsStore UserSettingsStore,
    Func<IReadOnlyList<string>> SerialPortProvider,
    Func<string, int, IPttSource> SerialPttFactory,
    IUiDispatcher UiDispatcher,
    bool NetworkDisabledDemo = false)
{
    public static DesktopRuntimeDependencies CreateDefault()
        => new(
            new UserSettingsStore(UserSettingsStore.DefaultPath),
            SerialPttSource.GetAvailablePortNames,
            (portName, baudRate) => new SerialPttSource(portName, baudRate),
            AvaloniaUiDispatcher.Instance);
}

internal sealed record ConsoleTopology(
    ConsoleConfiguration Configuration,
    string CodeplugPath,
    IReadOnlyList<string> ValidationErrors)
{
    public bool IsValid => ValidationErrors.Count == 0;
}

internal sealed record ConsoleSessionLoadResult(string StatusText, ConsoleTopology? Topology);

internal sealed class ConsoleSessionLoader
{
    private readonly UserSettingsStore settingsStore;

    public ConsoleSessionLoader(UserSettingsStore settingsStore)
    {
        this.settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
    }

    public ConsoleSessionLoadResult Load(string? configurationPath)
    {
        if (string.IsNullOrWhiteSpace(configurationPath))
            configurationPath = settingsStore.Load().LastCodeplugPath;

        if (string.IsNullOrWhiteSpace(configurationPath))
        {
            return new ConsoleSessionLoadResult(
                "No codeplug selected. Launch with a path to a codeplug YAML file.",
                null);
        }

        try
        {
            ConsoleConfiguration configuration = ConfigurationLoader.Load(configurationPath);
            IReadOnlyList<string> errors = ConfigurationLoader.Validate(configuration);
            string status = errors.Count == 0
                ? $"Loaded {configuration.Systems.Count} system(s) and {configuration.Zones.Count} zone(s). Connections are idle until Connect is pressed."
                : $"Configuration has {errors.Count} validation error(s):\n• {string.Join("\n• ", errors)}";
            string loadedCodeplugPath = configuration.SourcePath ?? Path.GetFullPath(configurationPath);
            return new ConsoleSessionLoadResult(
                status,
                new ConsoleTopology(configuration, loadedCodeplugPath, errors.ToArray()));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException or YamlDotNet.Core.YamlException)
        {
            return new ConsoleSessionLoadResult($"Unable to load codeplug: {exception.Message}", null);
        }
    }
}

internal sealed class ConsoleSessionFactory
{
    private readonly DesktopRuntimeDependencies dependencies;

    public ConsoleSessionFactory(DesktopRuntimeDependencies dependencies)
    {
        this.dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
    }

    public MainWindowViewModel Create(ConsoleSessionLoadResult loadResult)
    {
        ArgumentNullException.ThrowIfNull(loadResult);
        if (loadResult.Topology is null)
            return CreateEmpty(loadResult.StatusText);
        ConsoleTopology topology = loadResult.Topology;
        if (!topology.IsValid)
            return CreateRejected(loadResult.StatusText, topology);

        ConsoleConfiguration configuration = topology.Configuration;
        var services = new ConsoleSessionServices();
        return ConsoleSessionConstruction.Create(services, () =>
        {
            (P25KeyRing p25KeyRing, DmrKeyRing dmrKeyRing, NxdnKeyRing nxdnKeyRing) = LoadKeyRings(
                configuration,
                out string? keyWarning);
            services.Connection.Own("p25-key-ring", p25KeyRing);
            services.Connection.Own("dmr-key-ring", dmrKeyRing);
            services.Connection.Own("nxdn-key-ring", nxdnKeyRing);
            IReadOnlyList<ZoneViewModel> zones = CreateZones(
                configuration,
                p25KeyRing,
                dmrKeyRing,
                nxdnKeyRing);
            string status = string.IsNullOrWhiteSpace(keyWarning)
                ? loadResult.StatusText
                : $"{loadResult.StatusText}\n{keyWarning}";
            var viewModel = new MainWindowViewModel(
                status,
                CreateSystemViewModels(configuration, zones),
                zones,
                new MainWindowViewModelOptions(
                    P25KeyResolver: p25KeyRing,
                    UserSettingsStore: dependencies.UserSettingsStore,
                    GroupDefinitions: configuration.EffectiveGroups(),
                    PatchSourceIdPassthrough: configuration.PatchSourceIdPassthrough,
                    SerialPortProvider: dependencies.SerialPortProvider,
                    SerialPttFactory: dependencies.SerialPttFactory,
                    DmrKeyResolver: dmrKeyRing,
                    NxdnKeyResolver: nxdnKeyRing,
                    CodeplugPath: topology.CodeplugPath,
                    UiDispatcher: dependencies.UiDispatcher,
                    SessionServices: services,
                    NetworkDisabledDemo: dependencies.NetworkDisabledDemo));
            viewModel.RecordLoadedCodeplug(topology.CodeplugPath);
            return viewModel;
        });
    }

    private MainWindowViewModel CreateEmpty(string status)
    {
        var services = new ConsoleSessionServices();
        return ConsoleSessionConstruction.Create(services, () => new MainWindowViewModel(
            status,
            [],
            [],
            new MainWindowViewModelOptions(
                UserSettingsStore: dependencies.UserSettingsStore,
                GroupDefinitions: [],
                SerialPortProvider: dependencies.SerialPortProvider,
                SerialPttFactory: dependencies.SerialPttFactory,
                UiDispatcher: dependencies.UiDispatcher,
                SessionServices: services,
                NetworkDisabledDemo: dependencies.NetworkDisabledDemo)));
    }

    private MainWindowViewModel CreateRejected(string status, ConsoleTopology topology)
    {
        ConsoleConfiguration configuration = topology.Configuration;
        IReadOnlyList<ZoneViewModel> zones = CreateZones(configuration, null, null, null);
        var services = new ConsoleSessionServices();
        return ConsoleSessionConstruction.Create(services, () => new MainWindowViewModel(
            status,
            [],
            zones,
            new MainWindowViewModelOptions(
                UserSettingsStore: dependencies.UserSettingsStore,
                GroupDefinitions: configuration.EffectiveGroups(),
                PatchSourceIdPassthrough: configuration.PatchSourceIdPassthrough,
                SerialPortProvider: dependencies.SerialPortProvider,
                SerialPttFactory: dependencies.SerialPttFactory,
                CodeplugPath: topology.CodeplugPath,
                UiDispatcher: dependencies.UiDispatcher,
                SessionServices: services,
                NetworkDisabledDemo: dependencies.NetworkDisabledDemo)));
    }

    private static IReadOnlyList<ZoneViewModel> CreateZones(
        ConsoleConfiguration configuration,
        P25KeyRing? p25KeyRing,
        DmrKeyRing? dmrKeyRing,
        NxdnKeyRing? nxdnKeyRing)
        => configuration.Zones.Select(zone => new ZoneViewModel(
            zone.Name,
            zone.Channels.Select(channel => new ChannelViewModel(
                channel,
                p25KeyRing,
                configuration.Systems
                    .FirstOrDefault(system => system.Name.Equals(channel.System, StringComparison.OrdinalIgnoreCase))
                    ?.AliasIndex,
                dmrKeyRing,
                nxdnKeyRing)).ToArray(),
            zone.WebStreams.Select(stream => new WebStreamViewModel(stream)).ToArray(),
            zone.TabColor,
            zone.TabTextColor)).ToArray();

    private static (P25KeyRing P25, DmrKeyRing Dmr, NxdnKeyRing Nxdn) LoadKeyRings(
        ConsoleConfiguration configuration,
        out string? warning)
    {
        var p25Ring = new P25KeyRing();
        var dmrRing = new DmrKeyRing();
        var nxdnRing = new NxdnKeyRing();
        warning = null;
        if (string.IsNullOrWhiteSpace(configuration.KeyFile))
            return (p25Ring, dmrRing, nxdnRing);

        try
        {
            KeyContainer localKeys = KeyFileLoader.Load(
                ConfigurationLoader.ResolvePath(configuration, configuration.KeyFile));
            foreach (SystemConfiguration system in configuration.Systems)
            {
                p25Ring.AddLocalKeys(system.Name, localKeys);
                dmrRing.AddLocalKeys(system.Name, localKeys);
                nxdnRing.AddLocalKeys(system.Name, localKeys);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException or YamlDotNet.Core.YamlException)
        {
            warning = $"Encryption keys unavailable: {exception.Message} Clear receive remains available. Secure transmit and encrypted receive require the applicable key; P25 keys may arrive from FNE/KMM, while DMR and NXDN use local keys.";
            p25Ring.Dispose();
            dmrRing.Dispose();
            nxdnRing.Dispose();
            return (new P25KeyRing(), new DmrKeyRing(), new NxdnKeyRing());
        }
        catch
        {
            p25Ring.Dispose();
            dmrRing.Dispose();
            nxdnRing.Dispose();
            throw;
        }
        return (p25Ring, dmrRing, nxdnRing);
    }

    private static IReadOnlyList<SystemViewModel> CreateSystemViewModels(
        ConsoleConfiguration configuration,
        IReadOnlyList<ZoneViewModel> zones)
    {
        var channelsBySystem = new Dictionary<string, List<ChannelViewModel>>(StringComparer.OrdinalIgnoreCase);
        foreach (ChannelViewModel channel in zones.SelectMany(zone => zone.Channels))
        {
            if (!channelsBySystem.TryGetValue(channel.Definition.SystemName, out List<ChannelViewModel>? channels))
            {
                channels = [];
                channelsBySystem.Add(channel.Definition.SystemName, channels);
            }
            channels.Add(channel);
        }

        return OwnedResourceCollectionBuilder.Create(
            configuration.Systems.Count,
            systemIndex =>
        {
            SystemConfiguration system = configuration.Systems[systemIndex];
            IBrush systemAccent = SystemAccentPalette.GetBrush(systemIndex);
            IReadOnlyList<ZoneViewModel> systemZones = zones
                .Select(zone => new ZoneViewModel(
                    zone.Name,
                    zone.Channels.Where(channel => channel.Definition.SystemName.Equals(
                        system.Name,
                        StringComparison.OrdinalIgnoreCase)).ToArray(),
                    zone.WebStreams,
                    zone.TabColor,
                    zone.TabTextColor,
                    systemAccent))
                .Where(zone => zone.Channels.Count > 0)
                .ToArray();

            return new SystemViewModel(
                FneConnectionOptions.FromConfiguration(system),
                system.Name,
                $"{system.Address}:{system.Port}",
                channelsBySystem.TryGetValue(system.Name, out List<ChannelViewModel>? channels)
                    ? channels
                    : [],
                systemZones,
                systemIndex);
        });
    }
}

internal static class ConsoleSessionConstruction
{
    public static T Create<T>(ConsoleSessionServices services, Func<T> construct)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(construct);
        try
        {
            return construct();
        }
        catch (Exception constructionException)
        {
            try
            {
                services.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    "Console session construction and rollback both failed.",
                    constructionException,
                    cleanupException);
            }

            throw;
        }
    }
}
