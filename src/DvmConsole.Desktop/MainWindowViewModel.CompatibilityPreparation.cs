// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Audio;
using DvmConsole.Core.Settings;
using DvmConsole.Ptt;
using DvmConsole.Storage;
using DvmConsole.Vocoder;

namespace DvmConsole.Desktop;

public sealed partial class MainWindowViewModel
{
    private sealed record ShellPreparation(string StatusText, SystemViewModel[] Systems,
        ZoneViewModel[] Zones, MainWindowViewModelOptions Options);

    // Direct/demo shells supply existing radios and channel state. Adopt those identities
    // into the same graph factory without opening replacement transports or accepting an
    // unvalidated configuration through the public live-session entry point.
    private static ShellPreparation PrepareShell(string statusText, IEnumerable<SystemViewModel> systems,
        IEnumerable<ZoneViewModel> zones, MainWindowViewModelOptions? options)
    {
        options ??= new();
        var services = options.SessionServices ?? new ConsoleSessionServices();
        if (options.PreparedLiveSession is { } existing)
            return ConsoleSessionConstruction.CreateAsync(services, existing.State!.Terminal,
                _ => ValueTask.FromResult(new ShellPreparation(statusText, systems.ToArray(), zones.ToArray(), options)))
                .AsTask().GetAwaiter().GetResult();

        var terminal = options.PreparedState?.Terminal ?? new SessionTerminalFence();
        var systemViews = new List<SystemViewModel>();
        PreparedLiveSession? prepared = null;
        services.Connection.Register("systems", () => new ValueTask(DisposePreparedSystemsAsync(prepared, systemViews)));
        return ConsoleSessionConstruction.CreateAsync(services, terminal, _ =>
        {
            foreach (var system in systems) systemViews.Add(system);
            var zoneViews = zones.ToArray();
            var groups = options.GroupDefinitions?.ToArray() ?? [];
            var channels = systemViews.SelectMany(system => system.Channels)
                .Concat(zoneViews.SelectMany(zone => zone.Channels)).DistinctBy(channel => channel.Id).ToArray();
            var metadata = channels.Select(channel => new ConsoleSnapshotChannel(channel.SessionState,
                channel.AliasIndex, channel.ConfigurationAccess)).ToArray();
            var topology = options.PreparedTopology ?? DesktopConsoleSnapshotProjector.BuildTopology(
                systemViews, zoneViews, options.ConfigurationReference);
            if (options.PreparedTopology is null)
            {
                var descriptors = topology.Channels.ToDictionary(channel => channel.Id);
                topology = topology with { Channels = channels.Select(channel => descriptors[channel.Id]).ToArray() };
            }
            var state = options.PreparedState ?? ConsoleSessionState.AdoptExisting(topology, metadata,
                systemViews.ToDictionary(system => system.Id, system => system.KeyRequestState), terminal);
            var settingsStore = options.UserSettingsStore ?? new UserSettingsStore(UserSettingsStore.DefaultPath);
            var settings = settingsStore.Load();
            string path = string.IsNullOrWhiteSpace(options.CodeplugPath) ? string.Empty : Path.GetFullPath(options.CodeplugPath);
            if (options.ConfigurationReference is { } reference && path.Length > 0)
                ConfigurationOperatorStateStore.Activate(settings, reference.Id.ToString(), path,
                    options.MigrateLegacyConfigurationOperatorState);
            if (NormalizeHiddenAudioProcessingMode(settings)) settingsStore.Save(settings);
            var dependencies = new DesktopRuntimeDependencies(settingsStore,
                options.SerialPortProvider ?? SerialPttSource.GetAvailablePortNames,
                options.SerialPttFactory ?? ((port, baud) => new SerialPttSource(port, baud)),
                options.UiDispatcher ?? AvaloniaUiDispatcher.Instance,
                options.AssetStore ?? new ManagedAssetStore(Path.Combine(
                    Path.GetDirectoryName(settingsStore.Path) ?? AppContext.BaseDirectory, "Assets")),
                options.AudioBackendFactory ?? new DesktopAudioBackendFactory(Environment.GetEnvironmentVariable("DVM_AUDIO_LIBRARY")),
                options.VocoderFactory ?? new NativeVocoderFactory(), options.NetworkDisabledDemo);
            var runtime = options.OperationalRuntime ?? new ConsoleOperationalRuntime(services, metadata, state.Media, state.Terminal);
            prepared = new(runtime, state, settings, dependencies, services);
            RegisterSessionOwnership(services, prepared);
            prepared.InitializeMedia(systemViews.Select(system => (IRadioSession)system.RadioSession).ToArray(), dependencies,
                new TransmitKeyPort(options.P25KeyResolver, options.DmrKeyResolver, options.NxdnKeyResolver),
                options.PatchSourceIdPassthrough, groups, path,
                systemViews.ToDictionary(system => system.Id, system => system.Endpoint),
                systemViews.SelectMany(system => system.Channels).Select(channel => channel.SessionState)
                    .DistinctBy(channel => channel.Id).ToArray());
            return ValueTask.FromResult(new ShellPreparation(statusText, systemViews.ToArray(), zoneViews, options with
            {
                Features = (options.Features ?? new()) with { GroupDefinitions = groups },
                Document = (options.Document ?? new()) with
                {
                    PreparedState = state,
                    PreparedTopology = topology,
                    UserSettingsStore = dependencies.UserSettingsStore,
                    AssetStore = dependencies.AssetStore
                },
                Host = (options.Host ?? new()) with
                {
                    SessionServices = services,
                    OperationalRuntime = runtime,
                    PreparedLiveSession = prepared,
                    AudioBackendFactory = dependencies.AudioBackendFactory,
                    VocoderFactory = dependencies.VocoderFactory,
                    UiDispatcher = dependencies.UiDispatcher
                }
            }));
        }).AsTask().GetAwaiter().GetResult();
    }
}
