using DvmConsole.Core.Settings;
using Xunit;

namespace DvmConsole.Core.Tests;

public sealed class ConfigurationStudioSettingsTests
{
    [Fact]
    public void StudioStateIsScopedByNormalizedCodeplugPath()
    {
        var settings = new UserSettings();
        string path = Path.Combine(Path.GetTempPath(), "configuration-studio", "codeplug.yml");

        CodeplugStudioState first = CodeplugStudioStateStore.Get(settings, path);
        first.ZoneSystemAssignments["Empty Zone"] = "North";

        CodeplugStudioState same = CodeplugStudioStateStore.Get(settings, Path.GetFullPath(path));
        Assert.Same(first, same);
        Assert.Equal("North", same.ZoneSystemAssignments["Empty Zone"]);
    }

    [Fact]
    public void SaveAsCopiesStudioStateWithoutRemovingTheSource()
    {
        var settings = new UserSettings();
        string source = Path.Combine(Path.GetTempPath(), "configuration-studio", "source.yml");
        string destination = Path.Combine(Path.GetTempPath(), "configuration-studio", "copy.yml");
        CodeplugStudioStateStore.Get(settings, source).ZoneSystemAssignments["Empty Zone"] = "North";

        CodeplugStudioState copy = CodeplugStudioStateStore.CopyForSaveAs(settings, source, destination);
        copy.ZoneSystemAssignments["Empty Zone"] = "South";

        Assert.Equal("North", CodeplugStudioStateStore.Get(settings, source).ZoneSystemAssignments["Empty Zone"]);
        Assert.Equal("South", CodeplugStudioStateStore.Get(settings, destination).ZoneSystemAssignments["Empty Zone"]);
    }
}
