using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class DemoSessionTests
{
    [Fact]
    public void DemoArgumentIsNotTreatedAsAConfigurationPath()
    {
        string[] arguments = ["--demo", "--smoke-windows", "/tmp/operator-codeplug.yml"];

        Assert.Equal("/tmp/operator-codeplug.yml", Program.ReadConfigurationPath(arguments));
        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "Demo", "codeplug.yml"),
            App.ResolveDemoConfigurationPath(AppContext.BaseDirectory));
    }

    [Fact]
    public void DemoSessionStateUsesAndRemovesAnIsolatedTemporaryDirectory()
    {
        string parent = Path.Combine(Path.GetTempPath(), $"dvmconsole-demo-state-{Guid.NewGuid():N}");
        string root;
        try
        {
            using (DemoSessionState state = DemoSessionState.Create(parent))
            {
                root = Path.GetDirectoryName(state.UserSettingsPath)!;
                Assert.StartsWith(Path.GetFullPath(parent), state.UserSettingsPath, StringComparison.Ordinal);
                Assert.Equal(root, Path.GetDirectoryName(state.OperatorViewPath));
                Assert.True(Directory.Exists(root));
                File.WriteAllText(state.UserSettingsPath, "isolated");
            }

            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(parent))
                Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task BundledDemoIsBusyDeterministicAndCannotConnectOrTransmit()
    {
        string demoPath = App.ResolveDemoConfigurationPath(AppContext.BaseDirectory);
        string temporaryParent = Path.Combine(
            Path.GetTempPath(),
            $"dvmconsole-demo-host-{Guid.NewGuid():N}");
        Assert.True(File.Exists(demoPath), $"Missing bundled demo codeplug at {demoPath}");

        ConsoleConfiguration configuration = ConfigurationLoader.Load(demoPath);
        ZoneConfiguration expandedZone = Assert.Single(
            configuration.Zones,
            zone => zone.Name == "Campus Network");
        Assert.Equal(16, expandedZone.Channels.Count);
        Assert.Equal("Campus Dispatch", expandedZone.Channels[0].Name);
        Assert.Contains(expandedZone.Channels, channel => channel.SelectableEncryption);
        Assert.Equal(2, expandedZone.Channels.Count(channel => channel.RxOnly));

        try
        {
            using DemoSessionState state = DemoSessionState.Create(temporaryParent);
            var settingsStore = new UserSettingsStore(state.UserSettingsPath);
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(
                demoPath,
                settingsStore,
                serialPortProvider: static () => [],
                networkDisabledDemo: true);

            viewModel.InitializeDemoScenario();
            int historyCount = viewModel.CallHistory.Count;
            int recordingCount = viewModel.Recordings.Count;
            viewModel.InitializeDemoScenario();

            Assert.True(viewModel.IsCodeplugLoaded);
            Assert.True(viewModel.IsNetworkDisabledDemo);
            Assert.Equal(2, viewModel.Systems.Count);
            Assert.Equal(20, viewModel.Systems.SelectMany(system => system.Channels).Count());
            Assert.Contains(
                viewModel.AudioInputDevices,
                device => device.Name == "NEO Demo Microphone (synthetic)");
            Assert.Contains(
                viewModel.AudioOutputDevices,
                device => device.Name == "NEO Demo Output (synthetic)");
            Assert.All(
                viewModel.AudioInputDevices.Concat(viewModel.AudioOutputDevices),
                device => Assert.Contains(device.Id, new[] { "default", "neo-demo-input", "neo-demo-output" }));
            Assert.StartsWith(
                Path.GetDirectoryName(state.UserSettingsPath)!,
                Path.GetFullPath(viewModel.RecordingRootPathText),
                StringComparison.Ordinal);
            Assert.False(viewModel.ConnectCommand.CanExecute(null));
            Assert.Equal(historyCount, viewModel.CallHistory.Count);
            Assert.Equal(recordingCount, viewModel.Recordings.Count);
            Assert.True(viewModel.CallHistory.Count >= 8);
            Assert.Equal(2, viewModel.Recordings.Count);
            Assert.True(viewModel.CallHistory.Count(entry => entry.IsEvent) >= 3);
            Assert.Single(
                viewModel.Systems.SelectMany(system => system.Channels),
                channel => channel.IsTransmitting);
            Assert.True(viewModel.Systems.SelectMany(system => system.Channels).Count(channel => channel.IsReceivePresentationActive) >= 2);
            Assert.True(viewModel.Systems.SelectMany(system => system.Channels).Count(channel => channel.IsRecordingEnabled) >= 3);
            Assert.Contains("network", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("local pointer", viewModel.SelectionStatusText, StringComparison.OrdinalIgnoreCase);

            SystemViewModel system = viewModel.Systems[0];
            await viewModel.ToggleSystemConnectionAsync(system);
            Assert.False(system.IsConnected);
            Assert.Contains("no FNE connection", viewModel.TransmitStatusText, StringComparison.OrdinalIgnoreCase);

            ChannelViewModel idleChannel = viewModel.Systems
                .SelectMany(candidate => candidate.Channels)
                .First(channel => !channel.IsTransmitting && !channel.IsReceivePresentationActive);
            await viewModel.StartChannelTransmitAsync(idleChannel);
            Assert.False(idleChannel.IsTransmitting);
            Assert.Contains("network output remains disabled", viewModel.TransmitStatusText, StringComparison.OrdinalIgnoreCase);

            await viewModel.FlushUserSettingsAsync();
            Assert.True(File.Exists(state.UserSettingsPath));
            Assert.NotEqual(
                Path.GetFullPath(UserSettingsStore.DefaultPath),
                Path.GetFullPath(state.UserSettingsPath));
        }
        finally
        {
            if (Directory.Exists(temporaryParent))
                Directory.Delete(temporaryParent, recursive: true);
        }
    }
}
