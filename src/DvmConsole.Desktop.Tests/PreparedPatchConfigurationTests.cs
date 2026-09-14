// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Audio;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using DvmConsole.FneClient;
using DvmConsole.FneIntegration;
using DvmConsole.Storage;
using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class PreparedPatchConfigurationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SavedPatchMembershipAndStartupChoiceExistBeforeViews(bool restore)
    {
        string directory = Path.Combine(Path.GetTempPath(), "neo-prepared-patch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var settings = new UserSettings
        {
            RecordingRootPath = Path.Combine(directory, "recordings"),
            RetainPatchStateOnStartup = restore
        };
        var configuration = new ConsoleConfiguration
        {
            Systems = [new() { Name = "Test", Identity = "Test", Address = "127.0.0.1", Port = 62031, PeerId = 1, Rid = "1001" }],
            Zones = [new() { Name = "Operations", Channels =
            [
                new() { Name = "Source", System = "Test", Mode = "p25", Tgid = "100" },
                new() { Name = "Destination", System = "Test", Mode = "p25", Tgid = "101" }
            ] }]
        };
        var saved = new CodeplugGroupState
        {
            Memberships = new()
            {
                ["Dispatch"] =
            [
                new() { SystemName = "Test", ChannelName = "Source", DestinationId = 100 },
                new() { SystemName = "Test", ChannelName = "Destination", DestinationId = 101 }
            ]
            },
            EnabledStates = new() { ["Dispatch"] = true },
            OneWayModes = new() { ["Dispatch"] = true }
        };
        string configurationPath = Path.Combine(directory, "console.yml");
        settings.CodeplugGroupStates[configurationPath] = saved;
        var dependencies = new DesktopRuntimeDependencies(
            new UserSettingsStore(Path.Combine(directory, "settings.json")), () => [],
            (_, _) => throw new InvalidOperationException("Preparation cannot start hardware PTT."),
            ImmediateTestUiDispatcher.Instance, new ManagedAssetStore(Path.Combine(directory, "assets")),
            new UnusedAudioFactory(), new NativeVocoderFactory(), NetworkDisabledDemo: true);
        var state = ConsoleSessionState.Create(configuration);
        var services = new ConsoleSessionServices();
        var runtime = ConsoleOperationalRuntime.Prepare(services, state);
        try
        {
            var plan = FneConsoleRadioSessions.Prepare(state,
                configuration.Systems.Select(FneConnectionOptions.FromConfiguration),
                channel => new ChannelConfigurationAccess(channel.Runtime.Definition));
            var radios = await ConsoleRadioSessions.CreateAsync(state, plan, default);
            services.Connection.OwnAsync("test-radios", radios);
            var prepared = new MainWindowViewModel.PreparedLiveSession(runtime, state, settings, dependencies, services);
            MainWindowViewModel.RegisterSessionOwnership(services, prepared);
            prepared.InitializeMedia(radios.Sessions.Values.ToArray(), dependencies, new TransmitKeyPort(null, null, null), false,
                [new GroupConfiguration { Name = "Dispatch" }], configurationPath);

            Assert.Null(prepared.ViewModel);
            var group = Assert.Single(prepared.PatchConfiguration.SavedGroups);
            Assert.True(group.SavedEnabled);
            Assert.True(group.OneWay);
            Assert.Equal(2, group.Members.Length);
            Assert.Equal(0, group.UnresolvedMembers);
            Assert.True(Assert.Single(prepared.PatchConfiguration.MembershipIndex[group.Members[0]]).IsSource);
            Assert.False(Assert.Single(prepared.PatchConfiguration.MembershipIndex[group.Members[1]]).IsSource);
            Assert.True(Assert.Single(prepared.Snapshots.Capture().Channels[group.Members[0]].Patches).IsEnabled);
            if (restore) Assert.Equal(["Dispatch"], runtime.Patches.Forwarding.GroupNames);
            else Assert.Empty(runtime.Patches.Forwarding.GroupNames);
            Assert.All(runtime.Channels.Values, channel => Assert.False(channel.Operator.Snapshot.TransmitEnabled));

            prepared.PatchConfiguration.SetEnabled("Dispatch", false);
            prepared.PatchConfiguration.Apply();
            Assert.False(Assert.Single(prepared.Snapshots.Capture().Channels[group.Members[0]].Patches).IsEnabled);
            Assert.Empty(runtime.Patches.Forwarding.GroupNames);
        }
        finally
        {
            await services.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class UnusedAudioFactory : IAudioBackendFactory
    {
        public IAudioBackend Create(AudioBackendConfiguration configuration)
            => throw new InvalidOperationException("Patch restoration cannot open audio before connection.");
    }
}
