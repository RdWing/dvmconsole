using DvmConsole.Core.Configuration;
using DvmConsole.Core.Runtime;
using DvmConsole.Desktop;
using DvmConsole.FneClient;
using DvmConsole.Media;
using fnecore.P25;
using Avalonia.Media;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ChannelViewModelTests
{
    [Fact]
    public void StereoBalanceDefaultsToCenterAndClampsToTheSupportedRange()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "Test",
            Tgid = "100",
            Mode = "analog"
        });
        var property = typeof(ChannelViewModel).GetProperty("StereoBalance");

        Assert.NotNull(property);
        Assert.Equal(0.0, property.GetValue(channel));
        property.SetValue(channel, 2.0);
        Assert.Equal(1.0, property.GetValue(channel));
        Assert.Equal("Right", typeof(ChannelViewModel).GetProperty("StereoBalanceText")!.GetValue(channel));
    }

    [Fact]
    public void TransmitMutePreservesLogicalReceiveSelection()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "dmr",
            Slot = 1
        });

        channel.SetAudioEnabled(true);
        channel.SetAudioSuspended(true);

        Assert.True(channel.IsAudioEnabled);
        Assert.True(channel.IsAudioSuspended);
        Assert.Equal("RX muted", channel.AudioButtonText);
        Assert.Equal("RX muted during console transmit", channel.StateText);

        channel.SetAudioEnabled(true);

        Assert.True(channel.IsAudioEnabled);
        Assert.False(channel.IsAudioSuspended);
        Assert.Equal("Stop audio", channel.AudioButtonText);
    }

    [Fact]
    public void MatchingDmrVoiceTrafficUpdatesChannelRuntime()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "dmr",
            Slot = 2
        });
        channel.SetAudioEnabled(true);

        bool applied = channel.TryApplyTraffic(
            "System 1",
            CreateTraffic(FneTrafficProtocol.Dmr, 42, 99, 1, "VOICE", "VOICE", 7));

        Assert.True(applied);
        Assert.Equal(ChannelRuntimeState.Receiving, channel.State);
        Assert.Equal("Receiving from 42 (stream 7)", channel.StateText);
        Assert.Equal("42", channel.LastCallerText);
    }

    [Fact]
    public void ReceiveDisabledCardDoesNotPresentReceivingAudioState()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "dmr",
            Slot = 1
        });

        Assert.True(channel.TryApplyTraffic(
            "System 1",
            CreateTraffic(FneTrafficProtocol.Dmr, 42, 99, 0, "VOICE", "VOICE", 7)));

        Assert.Equal(ChannelRuntimeState.Receiving, channel.State);
        Assert.False(channel.IsReceivePresentationActive);
        Assert.Equal("Receive disabled", channel.StateText);
        Assert.NotEqual(
            Color.Parse("#008A3A"),
            Assert.IsType<SolidColorBrush>(channel.CardBackgroundBrush).Color);
    }

    [Fact]
    public void ReceiveCardStaysActiveUntilQueuedTerminatorIsProcessed()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "dmr",
            Slot = 1
        });
        channel.SetAudioEnabled(true);
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        channel.ApplyTraffic(
            "System 1",
            CreateTraffic(FneTrafficProtocol.Dmr, 42, 99, 0, "VOICE", "VOICE", 7),
            now);
        channel.MarkReceivePlaybackActive(42, 7);
        channel.ApplyTraffic(
            "System 1",
            CreateTraffic(FneTrafficProtocol.Dmr, 42, 99, 0, "DATA_SYNC", "TERMINATOR_WITH_LC", 7),
            now.AddSeconds(1));

        Assert.Equal(ChannelRuntimeState.Idle, channel.State);
        Assert.True(channel.IsReceivePresentationActive);
        Assert.Equal(
            Color.Parse("#008A3A"),
            Assert.IsType<SolidColorBrush>(channel.CardBackgroundBrush).Color);

        channel.MarkReceivePlaybackEnded(7);

        Assert.False(channel.IsReceivePresentationActive);
        Assert.NotEqual(
            Color.Parse("#008A3A"),
            Assert.IsType<SolidColorBrush>(channel.CardBackgroundBrush).Color);
    }

    [Fact]
    public void DmrVoiceLinkControlHeaderStartsAndCanReopenAReusedStream()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "dmr",
            Slot = 1
        });
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        Assert.Equal(
            ReceiveStreamTransition.Started,
            channel.ApplyTraffic(
                "System 1",
                CreateTraffic(FneTrafficProtocol.Dmr, 42, 99, 0, "DATA_SYNC", "VOICE_LC_HEADER", 7),
                now).Transition);
        Assert.Equal(
            ReceiveStreamTransition.Ended,
            channel.ApplyTraffic(
                "System 1",
                CreateTraffic(FneTrafficProtocol.Dmr, 42, 99, 0, "DATA_SYNC", "TERMINATOR_WITH_LC", 7),
                now.AddSeconds(1)).Transition);
        Assert.Equal(
            ReceiveStreamTransition.IgnoredLate,
            channel.ApplyTraffic(
                "System 1",
                CreateTraffic(FneTrafficProtocol.Dmr, 42, 99, 0, "VOICE", "VOICE", 7),
                now.AddSeconds(2)).Transition);
        Assert.Equal(
            ReceiveStreamTransition.Started,
            channel.ApplyTraffic(
                "System 1",
                CreateTraffic(FneTrafficProtocol.Dmr, 43, 99, 0, "DATA_SYNC", "VOICE_LC_HEADER", 7),
                now.AddSeconds(2.1)).Transition);
        Assert.Equal((uint)43, channel.SourceId);
    }

    [Fact]
    public void DisplaysConfiguredRadioAliasAlongsideSourceRid()
    {
        var channel = new ChannelViewModel(
            new ChannelConfiguration
            {
                Name = "Dispatch",
                System = "System 1",
                Tgid = "99",
                Mode = "analog"
            },
            aliases: [new RadioAlias { Alias = "Unit 42", Rid = 42 }]);
        channel.SetAudioEnabled(true);

        Assert.True(channel.TryApplyTraffic(
            "System 1",
            CreateTraffic(FneTrafficProtocol.Analog, 42, 99, null, "VOICE", "VOICE", 7)));

        Assert.Equal("Receiving from Unit 42 (42) (stream 7)", channel.StateText);
        Assert.Equal("Unit 42", channel.LastCallerText);
    }

    [Fact]
    public void LastCallerRemainsAliasOrRidAfterCallEnds()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "dmr",
            Slot = 1
        });

        Assert.True(channel.TryApplyTraffic(
            "System 1",
            CreateTraffic(FneTrafficProtocol.Dmr, 890, 99, 0, "VOICE", "VOICE", 7)));
        Assert.True(channel.TryApplyTraffic(
            "System 1",
            CreateTraffic(FneTrafficProtocol.Dmr, 890, 99, 0, "DATA_SYNC", "TERMINATOR_WITH_LC", 7)));

        Assert.Equal("890", channel.LastCallerText);
    }

    [Fact]
    public void AudioLevelTracksSamplesAndClearsWhenCallEnds()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "p25"
        });
        channel.SetAudioEnabled(true);
        Assert.True(channel.TryApplyTraffic(
            "System 1",
            CreateTraffic(FneTrafficProtocol.P25, 42, 99, null, "VOICE", "LDU1", 7)));

        channel.SetAudioLevel(ChannelAudioMeter.Calculate(
            Enumerable.Repeat((short)12000, 160).ToArray(),
            ChannelAudioDirection.Receive),
            ChannelAudioDirection.Receive);

        Assert.InRange(channel.AudioLevel, 1, 100);
        Assert.Equal(channel.AudioLevel / 100, channel.AudioLevelScale);
        Assert.True(channel.TryApplyTraffic(
            "System 1",
            CreateTraffic(FneTrafficProtocol.P25, 42, 0, null, "DATA_SYNC", "TDU", 7)));
        Assert.Equal(0, channel.AudioLevel);

        channel.SetAudioLevel(75, ChannelAudioDirection.Receive);
        Assert.Equal(0, channel.AudioLevel);
    }

    [Fact]
    public void ContinuedVoiceTrafficDoesNotRaiseNonVisualActivityNotifications()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "p25"
        });
        Assert.True(channel.TryApplyTraffic(
            "System 1",
            CreateTraffic(FneTrafficProtocol.P25, 42, 99, null, "VOICE", "LDU1", 7)));
        var changedProperties = new List<string?>();
        channel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        Assert.True(channel.TryApplyTraffic(
            "System 1",
            CreateTraffic(FneTrafficProtocol.P25, 42, 99, null, "VOICE", "LDU2", 7)));

        Assert.Empty(changedProperties);
    }

    [Fact]
    public void ReceiveMeterUsesMoreDisplayGainThanTransmitMeter()
    {
        short[] samples = Enumerable.Repeat((short)2000, 160).ToArray();

        double receive = ChannelAudioMeter.Calculate(samples, ChannelAudioDirection.Receive);
        double transmit = ChannelAudioMeter.Calculate(samples, ChannelAudioDirection.Transmit);

        Assert.True(receive > transmit);
        Assert.Equal(0, ChannelAudioMeter.Calculate([], ChannelAudioDirection.Receive));
    }

    [Fact]
    public void DmrTrafficFromWrongSystemDestinationSlotOrFrameIsIgnored()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "dmr",
            Slot = 2
        });

        Assert.False(channel.TryApplyTraffic("System 2", CreateTraffic(FneTrafficProtocol.Dmr, 42, 99, 1, "VOICE", "VOICE", 7)));
        Assert.False(channel.TryApplyTraffic("System 1", CreateTraffic(FneTrafficProtocol.Dmr, 42, 100, 1, "VOICE", "VOICE", 7)));
        Assert.False(channel.TryApplyTraffic("System 1", CreateTraffic(FneTrafficProtocol.Dmr, 42, 99, 0, "VOICE", "VOICE", 7)));
        Assert.False(channel.TryApplyTraffic("System 1", CreateTraffic(FneTrafficProtocol.Dmr, 42, 99, 1, "TERMINATOR", "TERMINATOR", 7)));
        Assert.Equal(ChannelRuntimeState.Idle, channel.State);
    }

    [Fact]
    public void MatchingTerminatorReturnsTheActiveChannelToIdle()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "dmr",
            Slot = 2
        });

        Assert.True(channel.TryApplyTraffic("System 1", CreateTraffic(FneTrafficProtocol.Dmr, 42, 99, 1, "VOICE", "VOICE", 7)));
        Assert.Equal(ChannelRuntimeState.Receiving, channel.State);

        Assert.True(channel.TryApplyTraffic("System 1", CreateTraffic(FneTrafficProtocol.Dmr, 42, 99, 1, "TERMINATOR", "TERMINATOR", 7)));

        Assert.Equal(ChannelRuntimeState.Idle, channel.State);
        Assert.Null(channel.StreamId);
    }

    [Fact]
    public void LateVoiceAfterTerminatorDoesNotReopenChannel()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "dmr",
            Slot = 2
        });
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        Assert.Equal(
            ReceiveStreamTransition.Started,
            channel.ApplyTraffic(
                "System 1",
                CreateTraffic(FneTrafficProtocol.Dmr, 42, 99, 1, "VOICE", "VOICE", 7),
                now).Transition);
        Assert.Equal(
            ReceiveStreamTransition.Ended,
            channel.ApplyTraffic(
                "System 1",
                CreateTraffic(FneTrafficProtocol.Dmr, 42, 99, 1, "TERMINATOR", "TERMINATOR", 7),
                now.AddSeconds(1)).Transition);
        Assert.Equal(
            ReceiveStreamTransition.IgnoredLate,
            channel.ApplyTraffic(
                "System 1",
                CreateTraffic(FneTrafficProtocol.Dmr, 42, 99, 1, "VOICE", "VOICE", 7),
                now.AddSeconds(2)).Transition);
        Assert.Equal(ChannelRuntimeState.Idle, channel.State);
    }

    [Fact]
    public void DmrDataSyncTerminatorWithLinkControlReturnsTheActiveChannelToIdle()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "dmr",
            Slot = 2
        });

        Assert.True(channel.TryApplyTraffic("System 1", CreateTraffic(
            FneTrafficProtocol.Dmr, 42, 99, 1, "VOICE", "VOICE", 7)));
        Assert.True(channel.TryApplyTraffic("System 1", CreateTraffic(
            FneTrafficProtocol.Dmr, 42, 99, 1, "DATA_SYNC", "TERMINATOR_WITH_LC", 7)));

        Assert.Equal(ChannelRuntimeState.Idle, channel.State);
    }

    [Fact]
    public void P25TduReturnsTheActiveChannelToIdle()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "p25"
        });

        Assert.True(channel.TryApplyTraffic("System 1", CreateTraffic(
            FneTrafficProtocol.P25, 42, 99, null, "VOICE", "LDU1", 7)));
        Assert.True(channel.TryApplyTraffic("System 1", CreateTraffic(
            FneTrafficProtocol.P25, 42, 99, null, "DATA_SYNC", "TDU", 7)));

        Assert.Equal(ChannelRuntimeState.Idle, channel.State);
    }

    [Fact]
    public void P25TduCanCloseActiveStreamWhenTerminatorOmitsDestination()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "p25"
        });

        Assert.True(channel.TryApplyTraffic("System 1", CreateTraffic(
            FneTrafficProtocol.P25, 42, 99, null, "VOICE", "LDU1", 7)));
        Assert.True(channel.TryApplyTraffic("System 1", CreateTraffic(
            FneTrafficProtocol.P25, 42, 0, null, "DATA_SYNC", "TDU", 7)));

        Assert.Equal(ChannelRuntimeState.Idle, channel.State);
    }

    [Fact]
    public void StaleReceiveStateWaitsThroughGraceBeforeExpiring()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "p25"
        });
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        Assert.Equal(ReceiveStreamTransition.Started, channel.ApplyTraffic(
            "System 1",
            CreateTraffic(FneTrafficProtocol.P25, 42, 99, null, "VOICE", "LDU1", 7),
            now).Transition);

        Assert.Equal(
            ReceiveStreamTransition.GraceStarted,
            channel.AdvanceReceiveLifecycle(now.AddSeconds(2)).Transition);
        Assert.Equal(ChannelRuntimeState.Receiving, channel.State);
        Assert.Equal(
            ReceiveStreamTransition.Resumed,
            channel.ApplyTraffic(
                "System 1",
                CreateTraffic(FneTrafficProtocol.P25, 42, 99, null, "VOICE", "LDU2", 7),
                now.AddSeconds(3)).Transition);
        Assert.Equal(
            ReceiveStreamTransition.GraceExpired,
            channel.AdvanceReceiveLifecycle(now.AddSeconds(7)).Transition);
        Assert.Equal(ChannelRuntimeState.Idle, channel.State);
    }

    [Fact]
    public void NewVoiceStreamSupersedesAndTombstonesTheOldStream()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "p25"
        });
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        channel.ApplyTraffic(
            "System 1",
            CreateTraffic(FneTrafficProtocol.P25, 42, 99, null, "VOICE", "LDU1", 10),
            now);
        ChannelTrafficApplyResult superseded = channel.ApplyTraffic(
            "System 1",
            CreateTraffic(FneTrafficProtocol.P25, 43, 99, null, "VOICE", "LDU1", 11),
            now.AddMilliseconds(100));

        Assert.Equal(ReceiveStreamTransition.Superseded, superseded.Transition);
        Assert.Equal((uint)10, superseded.EndedStreamId);
        Assert.Equal((uint)11, superseded.ActiveStreamId);
        Assert.Equal(
            ReceiveStreamTransition.IgnoredLate,
            channel.ApplyTraffic(
                "System 1",
                CreateTraffic(FneTrafficProtocol.P25, 42, 99, null, "VOICE", "LDU2", 10),
                now.AddSeconds(1)).Transition);
        Assert.Equal((uint)11, channel.StreamId);
    }

    [Fact]
    public void SelectingGlobalTransmitDoesNotChangeCardStatusColors()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "dmr",
            Slot = 1
        });
        Color background = Assert.IsType<SolidColorBrush>(channel.CardBackgroundBrush).Color;
        Color border = Assert.IsType<SolidColorBrush>(channel.CardBorderBrush).Color;

        channel.SetTransmitSelected(true);

        Assert.Equal(background, Assert.IsType<SolidColorBrush>(channel.CardBackgroundBrush).Color);
        Assert.Equal(border, Assert.IsType<SolidColorBrush>(channel.CardBorderBrush).Color);
        Assert.Equal("TX", channel.TransmitSelectionText);
    }

    [Fact]
    public void ActiveCardTextRemainsReadableInLightTheme()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "dmr",
            Slot = 1
        });

        Assert.Equal(Color.Parse("#18212B"), Assert.IsType<SolidColorBrush>(channel.CardTextBrush).Color);

        channel.SetTransmitEnabled(true, 7);

        Assert.Equal(Color.Parse("#FFFFFF"), Assert.IsType<SolidColorBrush>(channel.CardTextBrush).Color);
    }

    [Fact]
    public void PageSelectionIsIndependentFromGlobalTransmitSelection()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "analog"
        });

        channel.SetPageSelected(true);

        Assert.True(channel.IsPageSelected);
        Assert.False(channel.IsTransmitSelected);
        Assert.Equal("PAGE", channel.PageSelectionText);

        channel.SetPageSelected(false);

        Assert.False(channel.IsPageSelected);
        Assert.Equal("PAGE", channel.PageSelectionText);
    }

    [Fact]
    public void AlertSelectionIsIndependentAndUsesColorInsteadOfACheckmark()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "p25"
        });
        Color idle = Assert.IsType<SolidColorBrush>(channel.AlertSelectionBrush).Color;

        channel.SetAlertSelected(true);

        Assert.True(channel.IsAlertSelected);
        Assert.False(channel.IsPageSelected);
        Assert.False(channel.IsTransmitSelected);
        Assert.Equal("ALERT", channel.AlertSelectionText);
        Assert.NotEqual(idle, Assert.IsType<SolidColorBrush>(channel.AlertSelectionBrush).Color);
    }

    [Fact]
    public void StaleTerminatorCannotCloseAnotherActiveStream()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "p25"
        });

        Assert.True(channel.TryApplyTraffic("System 1", CreateTraffic(FneTrafficProtocol.P25, 42, 99, null, "VOICE", "LDU1", 8)));

        Assert.False(channel.TryApplyTraffic("System 1", CreateTraffic(FneTrafficProtocol.P25, 42, 99, null, "TERMINATOR", "TDU", 7)));

        Assert.Equal(ChannelRuntimeState.Receiving, channel.State);
        Assert.Equal((uint)8, channel.StreamId);
    }

    [Fact]
    public void MatchingP25LduTrafficUpdatesChannelRuntime()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "P25 Dispatch",
            System = "System 1",
            Tgid = "101",
            Mode = "p25"
        });
        channel.SetAudioEnabled(true);

        bool applied = channel.TryApplyTraffic(
            "System 1",
            CreateTraffic(FneTrafficProtocol.P25, 77, 101, null, "VOICE", "LDU2", 9));

        Assert.True(applied);
        Assert.Equal(ChannelRuntimeState.Receiving, channel.State);
        Assert.Equal("Receiving from 77 (stream 9)", channel.StateText);
    }

    [Fact]
    public void RxOnlyDmrChannelCannotTransmit()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Receive only",
            System = "System 1",
            Tgid = "99",
            Mode = "dmr",
            Slot = 1,
            RxOnly = true
        });

        Assert.False(channel.CanTransmit);
    }

    [Fact]
    public void ClearDmrPttIsEnabledWithoutAnnouncedTalkgroupRules()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "DMR Dispatch",
            System = "System 1",
            Tgid = "9990",
            Mode = "dmr",
            Slot = 1
        });

        // Announced FNE talkgroup rules are deliberately not part of channel
        // transmit eligibility. Only explicit codeplug RX-only policy applies.
        Assert.True(channel.CanTransmit);
    }

    [Fact]
    public void ClearP25ChannelCanTransmit()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Clear P25",
            System = "System 1",
            Tgid = "101",
            Mode = "p25",
            Algo = "none"
        });

        Assert.True(channel.CanTransmit);
        Assert.True(channel.CanListen);
    }

    [Fact]
    public void AnalogChannelCanListenAndTransmitWithoutEncryption()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Analog Dispatch",
            System = "System 1",
            Tgid = "101",
            Mode = "analog"
        });

        Assert.True(channel.CanListen);
        Assert.True(channel.CanTransmit);
    }

    [Fact]
    public void EncryptedOrUnknownChannelsCannotTransmit()
    {
        var encryptedP25 = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Encrypted P25",
            System = "System 1",
            Tgid = "101",
            Mode = "p25",
            Algo = "aes"
        });
        var encryptedDmr = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Encrypted DMR",
            System = "System 1",
            Tgid = "102",
            Mode = "dmr",
            Slot = 1,
            Algo = "unsupported"
        });

        Assert.False(encryptedP25.CanTransmit);
        Assert.False(encryptedDmr.CanTransmit);
        Assert.False(encryptedP25.CanListen);
        Assert.False(encryptedDmr.CanListen);
    }

    [Fact]
    public void EncryptedP25CanListenWhenTheConfiguredKeyResolves()
    {
        var keyRing = new P25KeyRing("System 1", new KeyContainer
        {
            Keys =
            [
                new KeyEntry
                {
                    KeyId = 0x50,
                    AlgId = P25Defines.P25_ALGO_AES,
                    Key = "00112233445566778899AABBCCDDEEFF"
                }
            ]
        });
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Encrypted P25",
            System = "System 1",
            Tgid = "101",
            Mode = "p25",
            Algo = "aes",
            KeyId = "0x50",
            SelectableEncryption = true
        }, keyRing);

        Assert.True(channel.CanListen);
        Assert.True(channel.CanTransmit);
        Assert.True(channel.CanToggleEncryption);
        Assert.True(channel.IsTransmitEncrypted);
        Assert.Equal("SECURE", channel.EncryptionButtonText);
        Assert.Equal(Color.Parse("#B45309"), Assert.IsType<SolidColorBrush>(channel.EncryptionSelectionBrush).Color);

        channel.EncryptionCommand.Execute(null);

        Assert.False(channel.IsTransmitEncrypted);
        Assert.Equal("CLEAR", channel.EncryptionButtonText);
        Assert.True(channel.CanTransmit);
    }

    [Fact]
    public void EncryptedP25RefreshesCapabilitiesAfterRuntimeKeyArrival()
    {
        var keyRing = new P25KeyRing();
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Runtime-key P25",
            System = "System 1",
            Tgid = "101",
            Mode = "p25",
            Algo = "aes",
            KeyId = "0x50"
        }, keyRing);

        Assert.False(channel.CanListen);
        Assert.False(channel.CanTransmit);

        keyRing.AddOrReplaceFromFne(
            "System 1",
            P25Defines.P25_ALGO_AES,
            0x50,
            Convert.FromHexString("00112233445566778899AABBCCDDEEFF"));
        channel.RefreshEncryptionState();

        Assert.True(channel.CanListen);
        Assert.True(channel.CanTransmit);
    }

    [Fact]
    public void SelectableClearP25UsesClearDefinitionForGeneratedTransmit()
    {
        var keyRing = new P25KeyRing("System 1", new KeyContainer
        {
            Keys =
            [
                new KeyEntry
                {
                    KeyId = 0x50,
                    AlgId = P25Defines.P25_ALGO_AES,
                    Key = "00112233445566778899AABBCCDDEEFF"
                }
            ]
        });
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Selectable P25",
            System = "System 1",
            Tgid = "101",
            Mode = "p25",
            Algo = "aes",
            KeyId = "0x50",
            SelectableEncryption = true
        }, keyRing);

        ChannelRuntimeDefinition secureDefinition = ChannelTransmitDefinitionFactory.Create(channel);
        P25TxEncryptionOptions secureOptions = Assert.IsType<P25TxEncryptionOptions>(
            ChannelTransmitDefinitionFactory.CreateEncryptionOptions(channel, secureDefinition, keyRing));
        Assert.True(secureDefinition.IsEncrypted);
        Assert.Equal(P25Defines.P25_ALGO_AES, secureOptions.AlgorithmId);
        Assert.Equal((ushort)0x50, secureOptions.KeyId);

        channel.RestoreTransmitEncryption(false);
        ChannelRuntimeDefinition clearDefinition = ChannelTransmitDefinitionFactory.Create(channel);

        Assert.False(clearDefinition.IsEncrypted);
        Assert.Null(ChannelTransmitDefinitionFactory.CreateEncryptionOptions(channel, clearDefinition, keyRing));
    }

    [Fact]
    public void SelectableDmrUsesConfiguredPrivacyOnlyWhileSecure()
    {
        var keyRing = new DmrKeyRing("System 1", new KeyContainer
        {
            Keys =
            [
                new KeyEntry
                {
                    Protocol = "dmr",
                    KeyId = 3,
                    AlgId = DmrPrivacyAlgorithms.DesOfb,
                    Key = "0011223344556677"
                }
            ]
        });
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Selectable DMR",
            System = "System 1",
            Tgid = "101",
            Mode = "dmr",
            Slot = 1,
            Algo = "des-ofb",
            KeyId = "3",
            SelectableEncryption = true
        }, dmrKeyResolver: keyRing);

        ChannelRuntimeDefinition secureDefinition = ChannelTransmitDefinitionFactory.Create(channel);
        DmrPrivacyOptions secureOptions = Assert.IsType<DmrPrivacyOptions>(
            ChannelTransmitDefinitionFactory.CreateDmrPrivacyOptions(channel, secureDefinition, keyRing));
        Assert.Equal(DmrPrivacyAlgorithms.DesOfb, secureOptions.AlgorithmId);
        Assert.Equal((byte)3, secureOptions.KeyId);

        channel.RestoreTransmitEncryption(false);
        ChannelRuntimeDefinition clearDefinition = ChannelTransmitDefinitionFactory.Create(channel);
        Assert.False(clearDefinition.IsEncrypted);
        Assert.Null(ChannelTransmitDefinitionFactory.CreateDmrPrivacyOptions(channel, clearDefinition, keyRing));
    }

    [Fact]
    public void SelectableNxdnUsesConfiguredPrivacyOnlyWhileSecure()
    {
        var keyRing = new NxdnKeyRing("System 1", new KeyContainer
        {
            Keys =
            [
                new KeyEntry
                {
                    Protocol = "nxdn",
                    KeyId = 3,
                    AlgId = NxdnPrivacyAlgorithms.Des,
                    Key = "0011223344556677"
                }
            ]
        });
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Selectable NXDN",
            System = "System 1",
            Tgid = "101",
            Mode = "nxdn",
            Algo = "des-ofb",
            KeyId = "3",
            SelectableEncryption = true
        }, nxdnKeyResolver: keyRing);

        ChannelRuntimeDefinition secureDefinition = ChannelTransmitDefinitionFactory.Create(channel);
        NxdnPrivacyOptions secureOptions = Assert.IsType<NxdnPrivacyOptions>(
            ChannelTransmitDefinitionFactory.CreateNxdnPrivacyOptions(channel, secureDefinition, keyRing));
        Assert.Equal(NxdnPrivacyAlgorithms.Des, secureOptions.AlgorithmId);
        Assert.Equal((byte)3, secureOptions.KeyId);

        channel.RestoreTransmitEncryption(false);
        ChannelRuntimeDefinition clearDefinition = ChannelTransmitDefinitionFactory.Create(channel);
        Assert.False(clearDefinition.IsEncrypted);
        Assert.Null(ChannelTransmitDefinitionFactory.CreateNxdnPrivacyOptions(channel, clearDefinition, keyRing));
    }

    [Fact]
    public void RestoresSelectableEncryptionStateWithoutChangingCodeplugPolicy()
    {
        var keyRing = new P25KeyRing("System 1", new KeyContainer
        {
            Keys =
            [
                new KeyEntry
                {
                    KeyId = 0x50,
                    AlgId = P25Defines.P25_ALGO_AES,
                    Key = "00112233445566778899AABBCCDDEEFF"
                }
            ]
        });
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Selectable P25",
            System = "System 1",
            Tgid = "101",
            Mode = "p25",
            Algo = "aes",
            KeyId = "0x50",
            SelectableEncryption = true
        }, keyRing);

        channel.RestoreTransmitEncryption(false);

        Assert.False(channel.IsTransmitEncrypted);
        Assert.True(channel.CanTransmit);
        Assert.True(channel.CanToggleEncryption);
    }

    private static FneTrafficFrame CreateTraffic(
        FneTrafficProtocol protocol,
        uint sourceId,
        uint destinationId,
        byte? slot,
        string frameType,
        string subtype,
        uint streamId)
    {
        return new FneTrafficFrame(
            protocol,
            peerId: 1,
            sourceId,
            destinationId,
            slot,
            callType: "GROUP",
            frameType,
            subtype,
            packetSequence: 1,
            streamId,
            payload: []);
    }
}
