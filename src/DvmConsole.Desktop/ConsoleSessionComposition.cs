// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Avalonia.Media;
using DvmConsole.Application;
using DvmConsole.Audio;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Ptt;
using DvmConsole.Storage;
using DvmConsole.Vocoder;

namespace DvmConsole.Desktop;

internal sealed record DesktopRuntimeDependencies(
    UserSettingsStore UserSettingsStore,
    Func<IReadOnlyList<string>> SerialPortProvider,
    Func<string, int, IPttSource> SerialPttFactory,
    IUiDispatcher UiDispatcher,
    IAssetStore AssetStore,
    IAudioBackendFactory AudioBackendFactory,
    IVocoderFactory VocoderFactory,
    bool NetworkDisabledDemo = false,
    Action<ConsoleSessionServiceDisposalTiming>? ServiceDisposalObserved = null)
{
    public static DesktopRuntimeDependencies CreateDefault()
    {
        var settingsStore = new UserSettingsStore(UserSettingsStore.DefaultPath);
        string appDataRoot = Path.GetDirectoryName(settingsStore.Path) ?? AppContext.BaseDirectory;
        return new(
            settingsStore,
            SerialPttSource.GetAvailablePortNames,
            (portName, baudRate) => new SerialPttSource(portName, baudRate),
            AvaloniaUiDispatcher.Instance,
            new ManagedAssetStore(Path.Combine(appDataRoot, "Assets")),
            new DesktopAudioBackendFactory(Environment.GetEnvironmentVariable("DVM_AUDIO_LIBRARY")),
            new NativeVocoderFactory());
    }
}

internal sealed class ConsoleSessionLoader
{
    private readonly UserSettingsStore settingsStore;

    public ConsoleSessionLoader(UserSettingsStore settingsStore)
    {
        this.settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
    }

    public ConsoleSessionLoadResult Load(
        string? configurationPath,
        ConfigurationReference? configurationReference = null,
        bool useLegacyPathFallback = true,
        bool migrateLegacyConfigurationOperatorState = false)
    {
        if (useLegacyPathFallback && string.IsNullOrWhiteSpace(configurationPath))
            configurationPath = settingsStore.Load().LastCodeplugPath;

        return ConfigurationSessionLoader.Load(configurationPath, configurationReference,
            migrateLegacyConfigurationOperatorState);
    }
}

internal sealed class ConsoleSessionFactory
{
    private readonly DesktopRuntimeDependencies dependencies;
    private readonly MainWindowSessionComposition sessionComposition = new();

    public ConsoleSessionFactory(DesktopRuntimeDependencies dependencies)
    {
        this.dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
    }

    public MainWindowViewModel Create(ConsoleSessionLoadResult loadResult)
        => CreateAsync(loadResult).AsTask().GetAwaiter().GetResult();

    public async ValueTask<MainWindowViewModel> CreateAsync(ConsoleSessionLoadResult loadResult,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loadResult);
        cancellationToken.ThrowIfCancellationRequested();
        if (loadResult.Topology is null)
            return CreateEmpty(loadResult.StatusText);
        ConsoleTopology topology = loadResult.Topology;
        if (!topology.IsValid)
            return CreateRejected(loadResult.StatusText, topology);

        ConsoleConfiguration configuration = topology.Configuration;
        UserSettings sessionSettings = dependencies.UserSettingsStore.Load();
        if (topology.ConfigurationReference is { } configurationReference)
        {
            ConfigurationOperatorStateStore.Activate(
                sessionSettings,
                configurationReference.Id.ToString(),
                topology.CodeplugPath,
                topology.MigrateLegacyConfigurationOperatorState);
        }
        CodeplugStudioState studioState = CodeplugStudioStateStore.Get(
            sessionSettings,
            topology.CodeplugPath);
        string? keyWarning = null;
        P25KeyRing p25KeyRing = null!;
        DmrKeyRing dmrKeyRing = null!;
        NxdnKeyRing nxdnKeyRing = null!;
        IReadOnlyList<SystemViewModel> systemViews = [];
        MainWindowViewModel.PreparedLiveSession? preparedLive = null;
        var request = new ConsoleLiveSessionRequest<MainWindowViewModel.PreparedLiveSession>(
            configuration, topology.ConfigurationReference, studioState.CallPrioritySystemNames,
            (state, ownership) =>
            {
                (p25KeyRing, dmrKeyRing, nxdnKeyRing) = ConfigurationKeyRingLoader.Load(configuration, out keyWarning);
                ownership.Connection.Own("p25-key-ring", p25KeyRing);
                ownership.Connection.Own("dmr-key-ring", dmrKeyRing);
                ownership.Connection.Own("nxdn-key-ring", nxdnKeyRing);
                return new TransmitKeyPort(p25KeyRing, dmrKeyRing, nxdnKeyRing);
            },
            (state, keys) => FneConsoleRadioSessions.Prepare(state,
                configuration.Systems.Select(FneConnectionOptions.FromConfiguration),
                channel => new ChannelConfigurationAccess(channel.Runtime.Definition, keys.P25, keys.Dmr, keys.Nxdn)),
            (context, token) =>
            {
                context.Services.Connection.Register("systems", () =>
                    new ValueTask(MainWindowViewModel.DisposePreparedSystemsAsync(preparedLive, systemViews)));
                preparedLive = new(context.Runtime, context.State, sessionSettings, dependencies, context.Services);
                MainWindowViewModel.RegisterSessionOwnership(context.Services, preparedLive);
                var ports = preparedLive.CreatePorts(context.Radios.Sessions.Values.ToArray(), dependencies,
                    context.Keys, configuration.PatchSourceIdPassthrough, configuration.EffectiveGroups(), topology.CodeplugPath,
                    configuration.Systems.ToDictionary(system => SystemId.FromName(system.Name), system => $"{system.Address}:{system.Port}"));
                return ValueTask.FromResult(new ConsoleLiveHostPreparation<MainWindowViewModel.PreparedLiveSession>(preparedLive, ports));
            }, dependencies.ServiceDisposalObserved);
        // Resume on the caller's UI context only after the shared graph is ready.
        ConsolePreparedLiveSession<MainWindowViewModel.PreparedLiveSession> prepared =
            await ConsoleLiveSessionFactory.CreateAsync(request, cancellationToken);
        ConsoleSessionServices services = prepared.Services;
        ConsoleSessionState preparedState = prepared.State;
        ConsoleOperationalRuntime runtime = prepared.Runtime;
        ConsoleRadioSessions radios = prepared.Radios;
        return await ConsoleSessionConstruction.CreateAsync(services, preparedState.Terminal, token =>
        {
            prepared.Host.BindPreparedRuntime(radios.Sessions.Values.ToArray(),
                new TransmitKeyPort(p25KeyRing, dmrKeyRing, nxdnKeyRing));
            token.ThrowIfCancellationRequested();
            IReadOnlyList<ZoneViewModel> zones = CreateZones(
                configuration,
                p25KeyRing,
                dmrKeyRing,
                nxdnKeyRing,
                preparedState);
            string status = string.IsNullOrWhiteSpace(keyWarning)
                ? loadResult.StatusText
                : $"{loadResult.StatusText}\n{keyWarning}";
            systemViews = CreateSystemViewModels(configuration, zones, studioState.ZoneSystemAssignments,
                studioState.CallPrioritySystemNames, preparedState, radios);
            token.ThrowIfCancellationRequested();
            var viewModel = new MainWindowViewModel(
                status,
                systemViews,
                zones,
                new MainWindowViewModelOptions(
                    Security: new(p25KeyRing, dmrKeyRing, nxdnKeyRing),
                    Document: new(
                        dependencies.UserSettingsStore,
                        dependencies.AssetStore,
                        topology.CodeplugPath,
                        topology.ConfigurationReference,
                        topology.MigrateLegacyConfigurationOperatorState,
                        preparedState.Topology,
                        preparedState),
                    Host: new(
                        dependencies.SerialPortProvider,
                        dependencies.SerialPttFactory,
                        dependencies.UiDispatcher,
                        services,
                        AudioBackendFactory: dependencies.AudioBackendFactory,
                        VocoderFactory: dependencies.VocoderFactory,
                        SessionComposition: sessionComposition, OperationalRuntime: runtime, PreparedLiveSession: preparedLive),
                    Features: new(
                        configuration.EffectiveGroups(),
                        configuration.PatchSourceIdPassthrough,
                        dependencies.NetworkDisabledDemo)));
            if (topology.ConfigurationReference is null)
                viewModel.RecordLoadedCodeplug(topology.CodeplugPath);
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(viewModel);
        }, cancellationToken).ConfigureAwait(false);
    }

    private MainWindowViewModel CreateEmpty(string status)
    {
        var services = new ConsoleSessionServices(dependencies.ServiceDisposalObserved);
        return ConsoleSessionConstruction.Create(services, () => new MainWindowViewModel(
            status,
            [],
            [],
            new MainWindowViewModelOptions(
                Document: new(dependencies.UserSettingsStore, dependencies.AssetStore),
                Host: new(
                    dependencies.SerialPortProvider,
                    dependencies.SerialPttFactory,
                    dependencies.UiDispatcher,
                    services,
                    AudioBackendFactory: dependencies.AudioBackendFactory,
                    VocoderFactory: dependencies.VocoderFactory,
                    SessionComposition: sessionComposition),
                Features: new([], NetworkDisabledDemo: dependencies.NetworkDisabledDemo))));
    }

    private MainWindowViewModel CreateRejected(string status, ConsoleTopology topology)
    {
        ConsoleConfiguration configuration = topology.Configuration;
        IReadOnlyList<ZoneViewModel> zones = CreateZones(configuration, null, null, null);
        var services = new ConsoleSessionServices(dependencies.ServiceDisposalObserved);
        return ConsoleSessionConstruction.Create(services, () => new MainWindowViewModel(
            status,
            [],
            zones,
            new MainWindowViewModelOptions(
                Document: new(
                    dependencies.UserSettingsStore,
                    dependencies.AssetStore,
                    topology.CodeplugPath,
                    topology.ConfigurationReference),
                Host: new(
                    dependencies.SerialPortProvider,
                    dependencies.SerialPttFactory,
                    dependencies.UiDispatcher,
                    services,
                    AudioBackendFactory: dependencies.AudioBackendFactory,
                    VocoderFactory: dependencies.VocoderFactory,
                    SessionComposition: sessionComposition),
                Features: new(
                    configuration.EffectiveGroups(),
                    configuration.PatchSourceIdPassthrough,
                    dependencies.NetworkDisabledDemo))));
    }

    private static IReadOnlyList<ZoneViewModel> CreateZones(
        ConsoleConfiguration configuration,
        P25KeyRing? p25KeyRing,
        DmrKeyRing? dmrKeyRing,
        NxdnKeyRing? nxdnKeyRing,
        ConsoleSessionState? preparedState = null)
        => configuration.Zones.Select(zone => new ZoneViewModel(
            zone.Name,
            zone.Channels.Select(channel => new ChannelViewModel(
                channel,
                p25KeyRing,
                preparedState?.Aliases[SystemId.FromName(channel.System)] ?? configuration.Systems
                    .FirstOrDefault(system => system.Name.Equals(channel.System, StringComparison.OrdinalIgnoreCase))
                    ?.AliasIndex,
                dmrKeyRing,
                nxdnKeyRing,
                preparedState?.Channels[ConsoleChannelState.GetId(
                    DvmConsole.Core.Runtime.ChannelRuntimeDefinition.FromConfiguration(channel))])).ToArray(),
            zone.WebStreams.Select(stream => new WebStreamViewModel(stream)).ToArray(),
            zone.TabColor,
            zone.TabTextColor)).ToArray();

    private static IReadOnlyList<SystemViewModel> CreateSystemViewModels(
        ConsoleConfiguration configuration,
        IReadOnlyList<ZoneViewModel> zones,
        IReadOnlyDictionary<string, string> zoneSystemAssignments,
        IReadOnlyCollection<string> callPrioritySystemNames,
        ConsoleSessionState preparedState,
        ConsoleRadioSessions radios)
    {
        return OwnedResourceCollectionBuilder.Create(
            configuration.Systems.Count,
            systemIndex =>
        {
            SystemConfiguration system = configuration.Systems[systemIndex];
            IBrush systemAccent = SystemAccentPalette.GetBrush(systemIndex);
            IReadOnlyList<ZoneViewModel> systemZones = zones
                .Select((zone, zoneIndex) => new ZoneViewModel(
                    zone.Name,
                    zone.Channels.Where(channel => channel.Definition.SystemName.Equals(
                        system.Name,
                        StringComparison.OrdinalIgnoreCase)).ToArray(),
                    IsZoneAssignedToSystem(
                        configuration,
                        configuration.Zones[zoneIndex],
                        system.Name,
                        zoneSystemAssignments)
                            ? zone.WebStreams
                            : [],
                    zone.TabColor,
                    zone.TabTextColor,
                    systemAccent))
                .Where(zone => zone.Channels.Count > 0 || zone.WebStreams.Count > 0)
                .ToArray();

            FneConnectionOptions options = FneConnectionOptions.FromConfiguration(system);
            IReadOnlyList<ChannelViewModel> systemChannels =
                zones.SelectMany(zone => zone.Channels).Where(channel =>
                    channel.Definition.SystemName.Equals(system.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            return new SystemViewModel(
                options,
                system.Name,
                $"{system.Address}:{system.Port}",
                systemChannels,
                systemZones,
                systemIndex,
                null,
                callPrioritySystemNames.Contains(system.Name, StringComparer.OrdinalIgnoreCase),
                (IFneRadioSession)radios.Sessions[SystemId.FromName(system.Name)],
                preparedState.KeyRequests[SystemId.FromName(system.Name)]);
        });
    }

    private static bool IsZoneAssignedToSystem(
        ConsoleConfiguration configuration,
        ZoneConfiguration zone,
        string systemName,
        IReadOnlyDictionary<string, string> zoneSystemAssignments)
    {
        if (zoneSystemAssignments.TryGetValue(zone.Name, out string? assignedSystem) &&
            !string.IsNullOrWhiteSpace(assignedSystem))
        {
            return assignedSystem.Equals(systemName, StringComparison.OrdinalIgnoreCase);
        }

        string[] channelSystems = zone.Channels
            .Select(channel => channel.System?.Trim() ?? string.Empty)
            .Where(candidate => candidate.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (channelSystems.Length == 1)
            return channelSystems[0].Equals(systemName, StringComparison.OrdinalIgnoreCase);
        if (channelSystems.Length > 1)
            return false;

        return configuration.Systems.FirstOrDefault()?.Name.Equals(
            systemName,
            StringComparison.OrdinalIgnoreCase) == true;
    }
}
