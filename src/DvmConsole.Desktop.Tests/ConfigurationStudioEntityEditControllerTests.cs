// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Core.Configuration;
using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ConfigurationStudioEntityEditControllerTests
{
    [Fact]
    public void AdditionalZoneUsesSelectedSystemInsteadOfFirstSystem()
    {
        var session = new TestSession();
        var controller = new ConfigurationStudioEntityEditController(session);
        controller.AddSystem();
        controller.AddSystem();
        SystemConfiguration selected = session.SelectedSystemForEdit!;

        controller.AddZone();

        Assert.Equal(selected.Name, session.GetZoneSystemName(session.SelectedZoneForEdit!));
    }

    [Fact]
    public void AddSystemCreatesAndSelectsItsInitialZoneAsOneMutation()
    {
        var session = new TestSession();
        var controller = new ConfigurationStudioEntityEditController(session);

        controller.AddSystem();

        SystemConfiguration system = Assert.Single(session.Configuration.Systems);
        ZoneConfiguration zone = Assert.Single(session.Configuration.Zones);
        Assert.Equal("New System", system.Name);
        Assert.Equal("New Zone", zone.Name);
        Assert.Equal(system.Name, session.GetZoneSystemName(zone));
        Assert.Same(system, session.SelectedSystemForEdit);
        Assert.Same(zone, session.SelectedZoneForEdit);
        Assert.Equal(1, session.MutationCount);
    }

    [Fact]
    public void DuplicateZonePreservesContentAndCreatesUniqueNames()
    {
        var session = new TestSession();
        var system = new SystemConfiguration { Name = "Regional" };
        var zone = new ZoneConfiguration
        {
            Name = "Dispatch",
            Channels =
            [
                new ChannelConfiguration
                {
                    Name = "Primary",
                    System = system.Name,
                    Tgid = "101",
                    CardSize = "large"
                }
            ],
            WebStreams =
            [
                new WebStreamConfiguration
                {
                    Name = "Scanner",
                    Url = "https://example.invalid/audio"
                }
            ]
        };
        session.Configuration.Systems.Add(system);
        session.Configuration.Zones.Add(zone);
        session.SetZoneSystemName(zone, system.Name);
        session.SelectZone(zone);
        var controller = new ConfigurationStudioEntityEditController(session);

        controller.DuplicateZone();

        ZoneConfiguration copy = Assert.IsType<ZoneConfiguration>(session.SelectedZoneForEdit);
        Assert.Equal("Dispatch Copy", copy.Name);
        Assert.Equal("Primary Copy", Assert.Single(copy.Channels).Name);
        Assert.Equal("101", copy.Channels[0].Tgid);
        Assert.Equal("large", copy.Channels[0].CardSize);
        Assert.Equal("Scanner Copy", Assert.Single(copy.WebStreams).Name);
        Assert.Equal(system.Name, session.GetZoneSystemName(copy));
        Assert.NotSame(zone.Channels[0], copy.Channels[0]);
        Assert.NotSame(zone.WebStreams[0], copy.WebStreams[0]);
    }

    [Fact]
    public void ReadOnlySessionRejectsEveryEntityMutation()
    {
        var session = new TestSession { CanEditEntities = false };
        var controller = new ConfigurationStudioEntityEditController(session);

        controller.AddSystem();
        controller.AddZone();
        controller.AddChannel();

        Assert.Empty(session.Configuration.Systems);
        Assert.Empty(session.Configuration.Zones);
        Assert.Equal(0, session.MutationCount);
    }

    [Fact]
    public void StreamCanBeAddedAndMovedBetweenZonesAsSingleMutations()
    {
        var session = new TestSession();
        var source = new ZoneConfiguration { Name = "Source" };
        var destination = new ZoneConfiguration { Name = "Destination" };
        session.Configuration.Zones.Add(source);
        session.Configuration.Zones.Add(destination);
        session.SelectZone(source);
        var controller = new ConfigurationStudioEntityEditController(session);

        controller.AddStream();
        WebStreamConfiguration stream = Assert.Single(source.WebStreams);
        Assert.Equal("New Stream", stream.Name);
        Assert.Same(stream, session.SelectedStreamForEdit?.Stream);

        controller.MoveSelectedStreamTo(destination);

        Assert.Empty(source.WebStreams);
        Assert.Same(stream, Assert.Single(destination.WebStreams));
        Assert.Same(destination, session.SelectedStreamForEdit?.Zone);
        Assert.Equal(2, session.MutationCount);
    }

    [Fact]
    public void GroupCommandsSelectAndRemoveTheCreatedGroup()
    {
        var session = new TestSession();
        var controller = new ConfigurationStudioEntityEditController(session);

        controller.AddGroup();

        GroupConfiguration group = Assert.Single(session.Configuration.Groups);
        Assert.Same(group, session.SelectedGroupForEdit);
        controller.DeleteGroup();

        Assert.Empty(session.Configuration.Groups);
        Assert.Equal(2, session.MutationCount);
    }

    private sealed class TestSession : IConfigurationStudioEntityEditSession
    {
        private readonly Dictionary<ZoneConfiguration, string> zoneSystems = [];

        public ConsoleConfiguration Configuration { get; } = new();
        public bool CanEditEntities { get; set; } = true;
        public SystemConfiguration? SelectedSystemForEdit { get; private set; }
        public ZoneConfiguration? SelectedZoneForEdit { get; private set; }
        public ChannelConfiguration? SelectedChannelForEdit { get; private set; }
        public ConfigurationStreamRow? SelectedStreamForEdit { get; private set; }
        public GroupConfiguration? SelectedGroupForEdit { get; private set; }
        public int MutationCount { get; private set; }
        public ConfigurationStudioSection SelectedSection { get; private set; }

        public string GetZoneSystemName(ZoneConfiguration zone)
            => zoneSystems.TryGetValue(zone, out string? value) ? value : string.Empty;

        public void SetZoneSystemName(ZoneConfiguration zone, string systemName)
            => zoneSystems[zone] = systemName;

        public void MutateEntities(Action mutation)
        {
            MutationCount++;
            mutation();
        }

        public void SelectSystem(SystemConfiguration? system)
            => SelectedSystemForEdit = system;

        public void SelectZone(ZoneConfiguration? zone)
            => SelectedZoneForEdit = zone;

        public void SelectChannel(ChannelConfiguration? channel)
            => SelectedChannelForEdit = channel;

        public void SelectStream(WebStreamConfiguration? stream)
        {
            SelectedStreamForEdit = stream is null
                ? null
                : Configuration.Zones
                    .SelectMany(zone => zone.WebStreams.Select(candidate => new ConfigurationStreamRow(zone, candidate)))
                    .FirstOrDefault(row => ReferenceEquals(row.Stream, stream));
        }

        public void SelectGroup(GroupConfiguration? group)
            => SelectedGroupForEdit = group;

        public void SelectSectionForEdit(ConfigurationStudioSection section)
            => SelectedSection = section;
    }
}
