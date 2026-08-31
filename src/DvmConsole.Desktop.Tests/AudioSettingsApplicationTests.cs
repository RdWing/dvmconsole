using DvmConsole.Application;
using DvmConsole.Core.Settings;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class AudioSettingsApplicationTests
{
    [Fact]
    public async Task PrivacyRequestsUseInjectedHostServiceWithoutPlatformTypes()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-audio-settings-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var permissions = new StubPrivacyPermissionService();
            await using var viewModel = new MainWindowViewModel(
                "Permission test",
                [],
                [],
                new MainWindowViewModelOptions(
                    UserSettingsStore: new UserSettingsStore(Path.Combine(root, "UserSettings.json")),
                    SerialPortProvider: () => [],
                    UiDispatcher: ImmediateUiDispatcher.Instance,
                    NetworkDisabledDemo: true,
                    PrivacyPermissionService: permissions));

            Assert.True(viewModel.IsMacOsPermissionRequestAvailable);
            viewModel.RequestMacOsKeyboardPermission();
            Assert.Contains("keyboard access requested", viewModel.AudioStatusText, StringComparison.OrdinalIgnoreCase);

            await viewModel.RequestMacOsMicrophonePermissionAsync();
            Assert.Contains("microphone access is denied", viewModel.AudioStatusText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, permissions.MicrophoneRequests);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FailedRouteChangeRollsBackRuntimeAndDoesNotPersistDraft()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-audio-settings-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string settingsPath = Path.Combine(root, "UserSettings.json");
        var store = new UserSettingsStore(settingsPath);
        UserSettings original = store.Load();
        var attemptedConfigurations = new List<ApplicationAudioConfiguration>();
        int attempts = 0;

        try
        {
            await using var viewModel = new MainWindowViewModel(
                "Audio settings test",
                [],
                [],
                new MainWindowViewModelOptions(
                    UserSettingsStore: store,
                    SerialPortProvider: () => [],
                    UiDispatcher: ImmediateUiDispatcher.Instance,
                    NetworkDisabledDemo: true,
                    ReconfigureApplicationAudio: configuration =>
                    {
                        attemptedConfigurations.Add(configuration);
                        if (Interlocked.Increment(ref attempts) == 1)
                            return Task.FromException(new IOException("synthetic route failure"));
                        return Task.CompletedTask;
                    }));
            viewModel.AudioInputDeviceIdText = "replacement-input";
            viewModel.AudioOutputDeviceIdText = "replacement-output";

            viewModel.ApplyAudioInputSettingsCommand.Execute(null);
            await WaitUntilAsync(
                () => viewModel.AudioStatusText.Contains(
                    "Unable to apply audio settings",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(2));

            Assert.Equal(2, attemptedConfigurations.Count);
            Assert.Equal("replacement-input", attemptedConfigurations[0].InputDeviceId);
            Assert.Equal(original.AudioInputDeviceId, attemptedConfigurations[1].InputDeviceId);
            Assert.True(viewModel.ApplyAudioInputSettingsCommand.CanExecute(null));
            UserSettings persisted = store.Load();
            Assert.Equal(original.AudioInputDeviceId, persisted.AudioInputDeviceId);
            Assert.Equal(original.AudioOutputDeviceId, persisted.AudioOutputDeviceId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!condition())
            await Task.Delay(10, cancellation.Token);
    }

    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public static ImmediateUiDispatcher Instance { get; } = new();

        public bool CheckAccess() => true;

        public void Post(Action action, bool background = false)
            => action();

        public ValueTask InvokeAsync(Action action)
        {
            action();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubPrivacyPermissionService : IDesktopPrivacyPermissionService
    {
        public bool IsMacOsPermissionRequestAvailable => true;
        public int MicrophoneRequests { get; private set; }

        public KeyboardPermissionState RequestKeyboardAccess()
            => KeyboardPermissionState.Requested;

        public ValueTask<MicrophonePermissionState> GetStateAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(MicrophonePermissionState.Denied);

        public ValueTask<MicrophonePermissionState> RequestAsync(
            CancellationToken cancellationToken = default)
        {
            MicrophoneRequests++;
            return ValueTask.FromResult(MicrophonePermissionState.Denied);
        }
    }
}
