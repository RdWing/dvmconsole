using Avalonia.Media;
using DvmConsole.Core.Settings;
using DvmConsole.Desktop;
using DvmConsole.Media;
using fnecore.P25;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed partial class SystemViewModelTests
{
    [Fact]
    public async Task ToolbarOutputMuteIsSessionScopedAndDescribesTarContinuation()
    {
        string codeplugPath = Path.Combine(AppContext.BaseDirectory, "TestData", "multiple-systems.yml");
        string settingsPath = CreateSettingsPath();
        try
        {
            await using MainWindowViewModel viewModel = MainWindowViewModel.Load(
                codeplugPath,
                new UserSettingsStore(settingsPath));

            Assert.False(viewModel.OutputMuted);
            Assert.Equal("🔊", viewModel.OutputMuteGlyph);

            viewModel.OutputMuted = true;

            Assert.True(viewModel.OutputMuted);
            Assert.Equal("🔇", viewModel.OutputMuteGlyph);
            Assert.Contains("TAR continues", viewModel.OutputMuteToolTip);
            Assert.Contains("TAR recording continue", viewModel.AudioStatusText);
        }
        finally
        {
            CleanupSettingsPath(settingsPath);
        }
    }

    [Theory]
    [InlineData("duplex", true, "duplex", false, true)]
    [InlineData("input-default", true, "output-default", true, true)]
    [InlineData("input", false, "output", false, false)]
    [InlineData("input-default", true, "output", false, false)]
    public void IdentifiesAppleVoiceProcessingCompatibleDevicePairs(
        string inputId,
        bool inputIsDefault,
        string outputId,
        bool outputIsDefault,
        bool expected)
    {
        var input = new AudioDeviceOptionViewModel(inputId, "Input", inputIsDefault);
        var output = new AudioDeviceOptionViewModel(outputId, "Output", outputIsDefault);

        Assert.Equal(expected, MainWindowViewModel.IsAppleVoiceProcessingDevicePairCompatible(input, output));
    }

    [Fact]
    public void PlansFneKeyRequestsEvenWhenLocalFallbackKeysAreAvailable()
    {
        const string aesKey = "00112233445566778899AABBCCDDEEFF";
        using var keyRing = new P25KeyRing("Alpha", new DvmConsole.Core.Configuration.KeyContainer
        {
            Keys =
            [
                new DvmConsole.Core.Configuration.KeyEntry
                {
                    KeyId = 0x50,
                    AlgId = P25Defines.P25_ALGO_AES,
                    Key = aesKey
                }
            ]
        });
        var secure = new ChannelViewModel(new DvmConsole.Core.Configuration.ChannelConfiguration
        {
            Name = "Secure",
            System = "Alpha",
            Tgid = "101",
            Mode = "p25",
            Algo = "aes",
            KeyId = "0x50"
        }, keyRing);
        var duplicate = new ChannelViewModel(new DvmConsole.Core.Configuration.ChannelConfiguration
        {
            Name = "Secure duplicate",
            System = "Alpha",
            Tgid = "102",
            Mode = "p25",
            Algo = "aes",
            KeyId = "0x50"
        }, keyRing);

        IReadOnlyList<(byte AlgorithmId, ushort KeyId)> requests =
            MainWindowViewModel.ResolveConfiguredP25KeyRequests([secure, duplicate]);

        Assert.Equal([(P25Defines.P25_ALGO_AES, (ushort)0x50)], requests);
        Assert.True(secure.CanListen);
    }

    [Fact]
    public void PlansFneKeyRequestsWithLegacyUnprefixedHexadecimalKeyIds()
    {
        var local20 = new ChannelViewModel(new DvmConsole.Core.Configuration.ChannelConfiguration
        {
            Name = "Legacy KID 20",
            System = "Alpha",
            Tgid = "101",
            Mode = "p25",
            Algo = "aes",
            KeyId = "20"
        });
        var alphanumeric = new ChannelViewModel(new DvmConsole.Core.Configuration.ChannelConfiguration
        {
            Name = "Legacy KID 069D",
            System = "Alpha",
            Tgid = "102",
            Mode = "p25",
            Algo = "aes",
            KeyId = "069D"
        });

        IReadOnlyList<(byte AlgorithmId, ushort KeyId)> requests =
            MainWindowViewModel.ResolveConfiguredP25KeyRequests([local20, alphanumeric]);

        Assert.Equal(
            [
                (P25Defines.P25_ALGO_AES, (ushort)0x0020),
                (P25Defines.P25_ALGO_AES, (ushort)0x069D)
            ],
            requests);
    }

    [Fact]
    public void ReportsUnreleasedSemanticVersion()
        => Assert.StartsWith("0.4.4", MainWindow.ApplicationVersion, StringComparison.Ordinal);

    [Theory]
    [InlineData("0.1.0-alpha.1+abcdef123456", "0.1.0-alpha.1 (abcdef1)")]
    [InlineData("0.1.0-alpha.1", "0.1.0-alpha.1")]
    [InlineData("0.1.0+abc", "0.1.0 (abc)")]
    public void FormatsCommitVersionLikeGitHub(string version, string expected)
        => Assert.Equal(expected, MainWindow.FormatShortVersion(version));

    [Fact]
    public void DefaultZoneColorsRemainReadableInBothThemes()
    {
        var zone = new ZoneViewModel("Dispatch", [], []);

        Assert.Equal(Color.Parse("#E8EDF3"), Assert.IsType<SolidColorBrush>(zone.TabBrush).Color);
        Assert.Equal(Color.Parse("#18212B"), Assert.IsType<SolidColorBrush>(zone.TabTextBrush).Color);

        zone.SetDarkMode(true);

        Assert.Equal(Color.Parse("#151D26"), Assert.IsType<SolidColorBrush>(zone.TabBrush).Color);
        Assert.Equal(Color.Parse("#DCE3EB"), Assert.IsType<SolidColorBrush>(zone.TabTextBrush).Color);
    }
}
