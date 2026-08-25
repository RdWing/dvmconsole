using DvmConsole.Core.Configuration;
using DvmConsole.Core.Settings;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class WebStreamSelectionIdentityTests
{
    [Fact]
    public void RequiresTheSameCodeplugUrlAndCredentialsForAutomaticRestore()
    {
        string codeplugPath = Path.Combine(Path.GetTempPath(), "trusted-codeplug.yml");
        WebStreamViewModel trusted = CreateStream(
            "Dispatch",
            "https://radio.example.test/live",
            "operator",
            "secret");
        string identity = WebStreamSelectionIdentity.Create(codeplugPath, trusted);

        Assert.True(WebStreamSelectionIdentity.IsAuthorized([identity], codeplugPath, trusted));
        Assert.False(WebStreamSelectionIdentity.IsAuthorized(
            [identity],
            Path.Combine(Path.GetTempPath(), "different-codeplug.yml"),
            trusted));
        Assert.False(WebStreamSelectionIdentity.IsAuthorized(
            [identity],
            codeplugPath,
            CreateStream("Dispatch", "http://127.0.0.1/private", "operator", "secret")));
        Assert.False(WebStreamSelectionIdentity.IsAuthorized(
            [identity],
            codeplugPath,
            CreateStream("Dispatch", "https://radio.example.test/live", "operator", "changed")));
        Assert.DoesNotContain("secret", identity, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyNameOnlySelectionDoesNotAuthorizeARequest()
    {
        string codeplugPath = Path.Combine(Path.GetTempPath(), "codeplug.yml");
        WebStreamViewModel stream = CreateStream("Dispatch", "https://radio.example.test/live");

        Assert.False(WebStreamSelectionIdentity.IsAuthorized(["Dispatch"], codeplugPath, stream));
    }

    [Fact]
    public async Task ChangedUrlAtTheSameCodeplugPathIsNotRestoredAutomatically()
    {
        string temporaryRoot = Path.Combine(Path.GetTempPath(), $"dvmconsole-web-selection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        string codeplugPath = Path.Combine(temporaryRoot, "codeplug.yml");
        string settingsPath = Path.Combine(temporaryRoot, "settings.json");
        var store = new UserSettingsStore(settingsPath);

        try
        {
            File.WriteAllText(codeplugPath, CreateCodeplug("https://radio.example.test/live"));
            await using (MainWindowViewModel trusted = MainWindowViewModel.Load(codeplugPath, store))
            {
                WebStreamViewModel stream = Assert.Single(trusted.WebStreams);
                stream.SetPlaybackState(true, false, true, false, "Live");
                await trusted.FlushUserSettingsAsync();
                string persisted = Assert.Single(store.Load().SelectedWebStreams);
                Assert.True(WebStreamSelectionIdentity.IsVersioned(persisted));
            }

            File.WriteAllText(codeplugPath, CreateCodeplug("http://127.0.0.1/private"));
            await using MainWindowViewModel changed = MainWindowViewModel.Load(codeplugPath, store);
            WebStreamViewModel changedStream = Assert.Single(changed.WebStreams);

            Assert.False(changedStream.IsActive);
            Assert.False(changedStream.IsConnecting);
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
                Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static WebStreamViewModel CreateStream(
        string name,
        string url,
        string? username = null,
        string? password = null)
        => new(new WebStreamConfiguration
        {
            Name = name,
            Url = url,
            AuthUsername = username,
            AuthPassword = password
        });

    private static string CreateCodeplug(string url)
        => $$"""
            systems: []
            zones:
              - name: Streams
                channels: []
                web_streams:
                  - name: Dispatch
                    url: "{{url}}"
            groups: []
            """;
}
