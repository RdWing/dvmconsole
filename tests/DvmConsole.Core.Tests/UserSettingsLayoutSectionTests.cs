// SPDX-License-Identifier: AGPL-3.0-only
using System.Reflection;
using dvmconsole;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DvmConsole.Core.Tests
{
    /// <summary>
    /// RED contract gate for the Core-owned layout-settings section DTO.
    /// Property names, value shapes, and defaults must remain compatible with
    /// the WPF SettingsManager layout properties.
    /// </summary>
    public sealed class UserSettingsLayoutSectionTests
    {
        [Fact]
        public void Type_IsPublicSealedWithWpfCompatibleProperties()
        {
            var type = typeof(UserSettingsLayoutSection);

            Assert.Equal("dvmconsole", type.Namespace);
            Assert.True(type.IsPublic);
            Assert.True(type.IsSealed);

            var properties = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .OrderBy(property => property.Name)
                .ToArray();

            Assert.Equal(
                new[]
                {
                    nameof(UserSettingsLayoutSection.AlertTonePositions),
                    nameof(UserSettingsLayoutSection.CanvasHeight),
                    nameof(UserSettingsLayoutSection.CanvasWidth),
                    nameof(UserSettingsLayoutSection.ChannelPositions),
                    nameof(UserSettingsLayoutSection.KeepWindowOnTop),
                    nameof(UserSettingsLayoutSection.LockWidgets),
                    nameof(UserSettingsLayoutSection.Maximized),
                    nameof(UserSettingsLayoutSection.ShowAlertTones),
                    nameof(UserSettingsLayoutSection.ShowChannels),
                    nameof(UserSettingsLayoutSection.ShowSystemStatus),
                    nameof(UserSettingsLayoutSection.SystemStatusPositions),
                    nameof(UserSettingsLayoutSection.UserBackgroundImage),
                    nameof(UserSettingsLayoutSection.WebStreamPositions),
                    nameof(UserSettingsLayoutSection.WindowHeight),
                    nameof(UserSettingsLayoutSection.WindowWidth)
                },
                properties.Select(property => property.Name));

            Assert.Equal(typeof(Dictionary<string, UserSettingsLayoutPosition>), properties[0].PropertyType);
            Assert.Equal(typeof(double), properties[1].PropertyType);
            Assert.Equal(typeof(double), properties[2].PropertyType);
            Assert.Equal(typeof(Dictionary<string, UserSettingsLayoutPosition>), properties[3].PropertyType);
            Assert.Equal(typeof(bool), properties[4].PropertyType);
            Assert.Equal(typeof(bool), properties[5].PropertyType);
            Assert.Equal(typeof(bool), properties[6].PropertyType);
            Assert.Equal(typeof(bool), properties[7].PropertyType);
            Assert.Equal(typeof(bool), properties[8].PropertyType);
            Assert.Equal(typeof(bool), properties[9].PropertyType);
            Assert.Equal(typeof(Dictionary<string, UserSettingsLayoutPosition>), properties[10].PropertyType);
            Assert.Equal(typeof(string), properties[11].PropertyType);
            Assert.Equal(typeof(Dictionary<string, UserSettingsLayoutPosition>), properties[12].PropertyType);
            Assert.Equal(typeof(double), properties[13].PropertyType);
            Assert.Equal(typeof(double), properties[14].PropertyType);
            Assert.All(properties, property =>
            {
                Assert.NotNull(property.GetMethod);
                Assert.NotNull(property.SetMethod);
                Assert.True(property.SetMethod!.IsPublic);
            });
        }

        [Fact]
        public void Position_IsPublicSealedWithMutableDoubleCoordinates()
        {
            var type = typeof(UserSettingsLayoutPosition);

            Assert.Equal("dvmconsole", type.Namespace);
            Assert.True(type.IsPublic);
            Assert.True(type.IsSealed);

            var properties = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .OrderBy(property => property.Name)
                .ToArray();

            Assert.Equal(new[] { nameof(UserSettingsLayoutPosition.X), nameof(UserSettingsLayoutPosition.Y) }, properties.Select(property => property.Name));
            Assert.All(properties, property => Assert.Equal(typeof(double), property.PropertyType));
            Assert.All(properties, property => Assert.True(property.SetMethod!.IsPublic));
        }

        [Fact]
        public void Defaults_MatchWpfMainWindowLayoutDefaults()
        {
            var section = new UserSettingsLayoutSection();

            Assert.Empty(section.ChannelPositions);
            Assert.Empty(section.SystemStatusPositions);
            Assert.Empty(section.AlertTonePositions);
            Assert.Empty(section.WebStreamPositions);
            Assert.Equal(875d, section.WindowWidth);
            Assert.Equal(700d, section.WindowHeight);
            Assert.Equal(875d, section.CanvasWidth);
            Assert.Equal(700d, section.CanvasHeight);
            Assert.False(section.KeepWindowOnTop);
            Assert.True(section.LockWidgets);
            Assert.False(section.Maximized);
            Assert.True(section.ShowAlertTones);
            Assert.True(section.ShowChannels);
            Assert.True(section.ShowSystemStatus);
            Assert.Null(section.UserBackgroundImage);
        }

        [Fact]
        public void SerializationAndRoundTrip_UseWpfCompatiblePascalCaseShape()
        {
            var section = new UserSettingsLayoutSection
            {
                ChannelPositions = new Dictionary<string, UserSettingsLayoutPosition>
                {
                    ["System|100"] = new UserSettingsLayoutPosition { X = 12.5, Y = 34.75 }
                },
                SystemStatusPositions = new Dictionary<string, UserSettingsLayoutPosition>
                {
                    ["System"] = new UserSettingsLayoutPosition { X = 1, Y = 2 }
                },
                AlertTonePositions = new Dictionary<string, UserSettingsLayoutPosition>
                {
                    ["alert.wav"] = new UserSettingsLayoutPosition { X = 3, Y = 4 }
                },
                WebStreamPositions = new Dictionary<string, UserSettingsLayoutPosition>
                {
                    ["stream"] = new UserSettingsLayoutPosition { X = 5, Y = 6 }
                },
                WindowWidth = 1200,
                WindowHeight = 900,
                CanvasWidth = 1180,
                CanvasHeight = 840,
                KeepWindowOnTop = true,
                LockWidgets = false,
                Maximized = true,
                ShowAlertTones = false,
                ShowChannels = false,
                ShowSystemStatus = false,
                UserBackgroundImage = "/tmp/background.png"
            };

            string json = JsonConvert.SerializeObject(section, Formatting.Indented);
            var objectValue = JObject.Parse(json);

            Assert.Equal(15, objectValue.Properties().Count());
            Assert.NotNull(objectValue[nameof(UserSettingsLayoutSection.ChannelPositions)]);
            Assert.NotNull(objectValue[nameof(UserSettingsLayoutSection.SystemStatusPositions)]);
            Assert.NotNull(objectValue[nameof(UserSettingsLayoutSection.AlertTonePositions)]);
            Assert.NotNull(objectValue[nameof(UserSettingsLayoutSection.WebStreamPositions)]);
            Assert.NotNull(objectValue[nameof(UserSettingsLayoutSection.WindowWidth)]);
            Assert.NotNull(objectValue[nameof(UserSettingsLayoutSection.WindowHeight)]);
            Assert.NotNull(objectValue[nameof(UserSettingsLayoutSection.CanvasWidth)]);
            Assert.NotNull(objectValue[nameof(UserSettingsLayoutSection.CanvasHeight)]);
            Assert.NotNull(objectValue[nameof(UserSettingsLayoutSection.KeepWindowOnTop)]);
            Assert.NotNull(objectValue[nameof(UserSettingsLayoutSection.LockWidgets)]);
            Assert.NotNull(objectValue[nameof(UserSettingsLayoutSection.Maximized)]);
            Assert.NotNull(objectValue[nameof(UserSettingsLayoutSection.ShowAlertTones)]);
            Assert.NotNull(objectValue[nameof(UserSettingsLayoutSection.ShowChannels)]);
            Assert.NotNull(objectValue[nameof(UserSettingsLayoutSection.ShowSystemStatus)]);
            Assert.NotNull(objectValue[nameof(UserSettingsLayoutSection.UserBackgroundImage)]);
            Assert.Null(objectValue["$type"]);
            Assert.DoesNotContain("channelPositions", json);

            var loaded = JsonConvert.DeserializeObject<UserSettingsLayoutSection>(json);

            Assert.NotNull(loaded);
            Assert.Equal(1200d, loaded!.WindowWidth);
            Assert.Equal(900d, loaded.WindowHeight);
            Assert.True(loaded.KeepWindowOnTop);
            Assert.False(loaded.LockWidgets);
            Assert.True(loaded.Maximized);
            Assert.False(loaded.ShowAlertTones);
            Assert.False(loaded.ShowChannels);
            Assert.False(loaded.ShowSystemStatus);
            Assert.Equal("/tmp/background.png", loaded.UserBackgroundImage);
            Assert.Equal(12.5, loaded.ChannelPositions["System|100"].X);
            Assert.Equal(34.75, loaded.ChannelPositions["System|100"].Y);
            Assert.Equal(6d, loaded.WebStreamPositions["stream"].Y);
        }
    }
}
