// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System.Collections.Generic;
using dvmconsole;
using DvmConsole.Avalonia.ViewModels;
using Avalonia.Media;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for Gate 2.2: project the complete codeplug channel
    /// identity and card presentation state onto every dynamic resource.
    /// Networking, audio and native control ownership stay outside the card.
    /// </summary>
    public sealed class FullCardIdentitySizingGateTests
    {
        [Fact]
        public void CodeplugChannel_ProjectsFullCardIdentityAndRxOnlyEligibility()
        {
            var vm = CreateVm(new Codeplug.Channel
            {
                Name = "Dispatch",
                System = "Repeater A",
                Tgid = "301",
                Mode = " dmr ",
                RxOnly = true,
                CardSize = "small",
                ResourceColor = "#123456",
            });

            var card = Assert.Single(vm.Channels);

            Assert.Equal("DMR", card.ChannelMode);
            Assert.Equal("Repeater A", card.SystemName);
            Assert.Equal("301", card.TalkgroupId);
            Assert.Equal("301", card.Talkgroup);
            Assert.True(card.IsRxOnly);
            Assert.False(card.IsPttEnabled);
            Assert.Equal(ChannelCardSize.Small, card.CardSize);
            Assert.Equal(154d, card.CardWidth);
            Assert.Equal(68d, card.CardHeight);
            Assert.Equal("#123456", card.IdleColor);
            Assert.Equal(Color.Parse("#123456"), Assert.IsType<SolidColorBrush>(card.IdleBrush).Color);
        }

        [Fact]
        public void CardSizeMapping_UsesWpfSmallNormalLargeDimensions()
        {
            var vm = CreateVm(
                Channel("Small", "small"),
                Channel("Normal", "normal"),
                Channel("Large", "large"));

            Assert.Equal(
                new[] { ChannelCardSize.Small, ChannelCardSize.Normal, ChannelCardSize.Large },
                new[] { vm.Channels[0].CardSize, vm.Channels[1].CardSize, vm.Channels[2].CardSize });
            Assert.Equal(new[] { 154d, 264d, 380d }, new[]
            {
                vm.Channels[0].CardWidth,
                vm.Channels[1].CardWidth,
                vm.Channels[2].CardWidth,
            });
            Assert.Equal(new[] { 68d, 110d, 158d }, new[]
            {
                vm.Channels[0].CardHeight,
                vm.Channels[1].CardHeight,
                vm.Channels[2].CardHeight,
            });
        }

        [Fact]
        public void MalformedCardPresentation_UsesSafeDefaults()
        {
            var vm = CreateVm(new Codeplug.Channel
            {
                Name = "Fallback",
                System = "Repeater A",
                Tgid = "302",
                Mode = "future-mode",
                CardSize = "wide",
                ResourceColor = "not-a-color",
            });

            var card = Assert.Single(vm.Channels);

            Assert.Equal("P25", card.ChannelMode);
            Assert.Equal(ChannelCardSize.Normal, card.CardSize);
            Assert.Equal(264d, card.CardWidth);
            Assert.Equal(110d, card.CardHeight);
            Assert.Equal(ChannelSlotViewModel.DefaultIdleColor, card.IdleColor);
            Assert.Equal(
                Color.Parse(ChannelSlotViewModel.DefaultIdleColor),
                Assert.IsType<SolidColorBrush>(card.IdleBrush).Color);
            Assert.False(card.IsRxOnly);
            Assert.True(card.IsPttEnabled);
        }

        [Fact]
        public void UnassignedCompatibilitySlot_UsesFullIdentityDefaults()
        {
            var vm = new MainWindowViewModel();
            var card = Assert.Single(vm.Channels, channel => channel.Number == 1);

            Assert.Equal(string.Empty, card.ChannelMode);
            Assert.Equal(string.Empty, card.SystemName);
            Assert.Equal("NO TALKGROUP", card.TalkgroupId);
            Assert.False(card.IsRxOnly);
            Assert.True(card.IsPttEnabled);
            Assert.Equal(ChannelCardSize.Normal, card.CardSize);
            Assert.Equal(ChannelSlotViewModel.DefaultIdleColor, card.IdleColor);
        }

        [Fact]
        public void RxOnlyPrimary_IsRejectedByPttTargetResolution()
        {
            var slot = new ChannelSlotViewModel(1, "CHANNEL 01");
            slot.Reassign(
                "Receive only",
                "501",
                "repeater a|501",
                "dmr",
                "Repeater A",
                isRxOnly: true,
                cardSize: "normal",
                idleColor: "#234567");
            var fallback = new ChannelSlotViewModel(2, "CHANNEL 02");
            fallback.Reassign(
                "Transmit",
                "502",
                "repeater a|502",
                "dmr",
                "Repeater A",
                isRxOnly: false,
                cardSize: "normal",
                idleColor: "#234567");
            var ptt = new PttCapabilityViewModel(
                new DvmConsole.Platform.Hotkeys.UnavailableGlobalHotkeyService(),
                () => slot,
                () => new[] { slot, fallback });
            ptt.AllChannels = true;

            ptt.PttPointerDown();

            Assert.False(ptt.IsEngaged);
            Assert.False(slot.PttEngaged);
        }

        private static MainWindowViewModel CreateVm(params Codeplug.Channel[] channels)
        {
            var codeplug = new Codeplug
            {
                Systems = new List<Codeplug.System>
                {
                    new Codeplug.System { Name = "Repeater A", Rid = "1000001" },
                },
                Zones = new List<Codeplug.Zone>
                {
                    new Codeplug.Zone
                    {
                        Name = "Zone A",
                        Channels = new List<Codeplug.Channel>(channels),
                    },
                },
            };

            return new MainWindowViewModel(
                codeplug.Systems,
                null,
                null,
                null,
                null,
                codeplug);
        }

        private static Codeplug.Channel Channel(string name, string cardSize)
            => new Codeplug.Channel
            {
                Name = name,
                System = "Repeater A",
                Tgid = name == "Small" ? "401" : name == "Normal" ? "402" : "403",
                Mode = "p25",
                CardSize = cardSize,
                ResourceColor = "#234567",
            };
    }
}
