using DvmConsole.Core.Settings;
using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ToneWorkspaceViewModelTests
{
    [Fact]
    public void InitializesEditableToneStateAndStableCollectionsFromSettings()
    {
        var settings = new UserSettings
        {
            LastDtmfDigits = "12#",
            ToneFrequencyHz = 1_234.5,
            ToneDurationSeconds = 0.75,
            QuickCallToneAFrequencyHz = 600,
            QuickCallToneBFrequencyHz = 1_200
        };
        var workspace = new ToneWorkspaceViewModel(settings);
        string? changedProperty = null;
        workspace.PropertyChanged += (_, args) => changedProperty = args.PropertyName;

        workspace.DtmfDigits = "9*";

        Assert.Equal(nameof(ToneWorkspaceViewModel.DtmfDigits), changedProperty);
        Assert.Equal("1234.5", workspace.ToneFrequencyText);
        Assert.Equal("0.75", workspace.ToneDurationText);
        Assert.Single(workspace.ToneSequenceSteps);
        Assert.Equal(3, workspace.BuiltInAlertTones.Count);
    }
}
