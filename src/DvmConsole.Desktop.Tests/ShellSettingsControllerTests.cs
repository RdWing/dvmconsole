// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Settings;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ShellSettingsControllerTests
{
    [Fact]
    public async Task ScheduleCapturesConfigurationStateBeforePersistingLatestSnapshot()
    {
        string root = NewRoot();
        var store = new UserSettingsStore(Path.Combine(root, "UserSettings.json"));
        var settings = new UserSettings();
        var port = new RecordingPort(() => settings.LastSelectedSystemName = "Captured");
        await using var controller = new ShellSettingsController(store, settings, port);
        try
        {
            controller.Schedule();
            await controller.FlushAsync();

            Assert.Equal(1, port.CaptureCount);
            Assert.Equal("Captured", store.Load().LastSelectedSystemName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProfileCommandsOwnStatusAndProfileInvalidation()
    {
        string root = NewRoot();
        var store = new UserSettingsStore(Path.Combine(root, "UserSettings.json"));
        var port = new RecordingPort();
        await using var controller = new ShellSettingsController(store, new UserSettings(), port);
        try
        {
            controller.SaveNamedProfile("Night");
            Assert.Equal(1, port.ProfileNotifications);
            Assert.Equal("Settings profile 'Night' saved.", port.Status);
            Assert.Contains("Night", controller.NamedProfiles);

            controller.DeleteNamedProfile("Night");
            Assert.Equal(2, port.ProfileNotifications);
            Assert.Equal("Settings profile 'Night' deleted.", port.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-shell-settings-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class RecordingPort(Action? capture = null) : IShellSettingsSessionPort
    {
        public bool IsClosed { get; set; }
        public int CaptureCount { get; private set; }
        public int ProfileNotifications { get; private set; }
        public string? Status { get; private set; }
        public void CaptureConfigurationState()
        {
            CaptureCount++;
            capture?.Invoke();
        }
        public void NotifyProfilesChanged() => ProfileNotifications++;
        public void PublishStatus(string text) => Status = text;
        public void ReportPersistenceFailure(Exception exception) => throw exception;
    }
}
