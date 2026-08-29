using DvmConsole.Core.Configuration;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ConfigurationIdentityMigrationPlannerTests
{
    [Fact]
    public void DeleteAndAddAtTheSameIndexDoesNotBecomeARename()
    {
        ConsoleConfiguration configuration = CreateConfiguration();
        var identities = new ConfigurationDraftIdentityRegistry();
        identities.RegisterInitial(configuration);
        var planner = new ConfigurationIdentityMigrationPlanner(configuration, identities);

        configuration.Systems.RemoveAt(0);
        configuration.Systems.Insert(0, new SystemConfiguration { Name = "Replacement" });
        configuration.Groups.RemoveAt(0);
        configuration.Groups.Insert(0, new GroupConfiguration { Name = "Replacement Group", Type = "patch" });
        configuration.Zones[0].Channels.RemoveAt(0);
        configuration.Zones[0].Channels.Insert(0, new ChannelConfiguration
        {
            Name = "Replacement Channel",
            System = "Replacement",
            Tgid = "200",
            Mode = "p25"
        });
        identities.Synchronize(configuration);

        Assert.Empty(planner.BuildSystemRenames());
        Assert.Equal(["Original System"], planner.BuildDeletedSystems());
        (Dictionary<string, string> groupRenames, IReadOnlyList<string> deletedGroups) = planner.BuildGroupMigrations();
        Assert.Empty(groupRenames);
        Assert.Equal(["Original Group"], deletedGroups);
        ChannelIdentityMigration migration = Assert.Single(planner.BuildChannelMigrations());
        Assert.Equal("Original Channel", migration.Original.Name);
        Assert.Null(migration.Current);
    }

    [Fact]
    public void ReorderAndRenameKeepTheOriginalIdentity()
    {
        ConsoleConfiguration configuration = CreateConfiguration();
        configuration.Systems.Add(new SystemConfiguration { Name = "Second System" });
        var identities = new ConfigurationDraftIdentityRegistry();
        identities.RegisterInitial(configuration);
        var planner = new ConfigurationIdentityMigrationPlanner(configuration, identities);

        SystemConfiguration original = configuration.Systems[0];
        configuration.Systems.Remove(original);
        configuration.Systems.Add(original);
        original.Name = "Renamed System";
        identities.Synchronize(configuration);

        KeyValuePair<string, string> rename = Assert.Single(planner.BuildSystemRenames());
        Assert.Equal("Original System", rename.Key);
        Assert.Equal("Renamed System", rename.Value);
        Assert.Empty(planner.BuildDeletedSystems());
    }

    [Fact]
    public void CapturedIdentitiesSurviveDraftReparse()
    {
        ConsoleConfiguration configuration = CreateConfiguration();
        var originalIdentities = new ConfigurationDraftIdentityRegistry();
        originalIdentities.RegisterInitial(configuration);
        ConfigurationDraftIdentityLayout layout = originalIdentities.Capture(configuration);

        ConsoleConfiguration reparsed = ConfigurationDocument.Parse(
            ConfigurationDocument.CreateNew().Serialize(),
            sourcePath: null).Configuration;
        reparsed.Systems.Clear();
        reparsed.Zones.Clear();
        reparsed.Groups.Clear();
        reparsed.Systems.Add(new SystemConfiguration { Name = "Original System" });
        reparsed.Zones.Add(new ZoneConfiguration
        {
            Name = "Original Zone",
            Channels =
            [
                new ChannelConfiguration
                {
                    Name = "Original Channel",
                    System = "Original System",
                    Tgid = "100",
                    Mode = "p25"
                }
            ]
        });
        reparsed.Groups.Add(new GroupConfiguration { Name = "Original Group", Type = "patch" });
        var restoredIdentities = new ConfigurationDraftIdentityRegistry();
        restoredIdentities.Restore(reparsed, layout);

        Assert.Equal(layout.SystemIds[0], restoredIdentities.GetSystemId(reparsed.Systems[0]));
        Assert.Equal(layout.Zones[0].ZoneId, restoredIdentities.GetZoneId(reparsed.Zones[0]));
        Assert.Equal(layout.Zones[0].ChannelIds[0], restoredIdentities.GetChannelId(reparsed.Zones[0].Channels[0]));
        Assert.Equal(layout.GroupIds[0], restoredIdentities.GetGroupId(reparsed.Groups[0]));
    }

    private static ConsoleConfiguration CreateConfiguration()
        => new()
        {
            Systems = [new SystemConfiguration { Name = "Original System" }],
            Zones =
            [
                new ZoneConfiguration
                {
                    Name = "Original Zone",
                    Channels =
                    [
                        new ChannelConfiguration
                        {
                            Name = "Original Channel",
                            System = "Original System",
                            Tgid = "100",
                            Mode = "p25"
                        }
                    ]
                }
            ],
            Groups = [new GroupConfiguration { Name = "Original Group", Type = "patch" }]
        };
}
