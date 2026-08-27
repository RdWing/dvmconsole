using DvmConsole.Core.Settings;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class AudioSettingsApplicationTests
{
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

        public ValueTask InvokeAsync(Action action)
        {
            action();
            return ValueTask.CompletedTask;
        }
    }
}
