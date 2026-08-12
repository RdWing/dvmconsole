// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dvmconsole;
using DvmConsole.Avalonia.Services;
using DvmConsole.Platform.Audio;
using Xunit;

namespace DvmConsole.Avalonia.Tests
{
    /// <summary>
    /// RED contract for Gate 4.3: compose the Core PatchManager into the
    /// Avalonia receive lifecycle without changing Core, Platform, the audio
    /// router, or the receive-glue signatures.
    /// </summary>
    public sealed class PatchForwardingCompositionContractTests
    {
        private const string Context = "/configs/codeplug.yml";

        [Fact]
        public void TwoWayDmr_ReceivesThreePcmFrames_ForwardsAndEndsExactlyOnce()
        {
            var codeplug = MakeCodeplug(passthrough: false);
            var encoder = new RecordingEncoder();
            var sender = new RecordingSender();
            using var coordinator = CreateCoordinator(codeplug, encoder, sender);
            coordinator.ApplySavedMemberships(MakeSection(
                patchMembers: ("S1", "101", "S2", "202"),
                patchEnabled: true,
                oneWay: false));

            coordinator.HandleReceiveFrame(Metadata("S1", 42, 101, 7, VoiceMode.Dmr));
            coordinator.ObserveDecodedPcm("s1|101|slot:0", VoiceMode.Dmr, Pcm());
            coordinator.ObserveDecodedPcm("s1|101|slot:0", VoiceMode.Dmr, Pcm());
            coordinator.ObserveDecodedPcm("s1|101|slot:0", VoiceMode.Dmr, Pcm());

            var voice = Assert.Single(sender.DmrVoiceCalls);
            Assert.Equal("S2", voice.Target.SystemName);
            Assert.Equal("202", voice.Target.TalkgroupId);
            Assert.Equal((byte)2, voice.Target.Slot);
            Assert.Equal(1u, voice.StreamId);
            Assert.Equal(0, voice.SeqNo);
            Assert.Equal(27, voice.Payload.Length);
            Assert.Equal(2002u, voice.Target.SourceId);

            coordinator.HandleReceiveFrame(Metadata("S1", 42, 101, 7, VoiceMode.Dmr, terminator: true));
            coordinator.HandleStreamEnded("s1|101|slot:0", VoiceMode.Dmr);

            Assert.Single(sender.DmrTerminatorCalls);
            Assert.Equal(1u, sender.DmrTerminatorCalls[0].StreamId);
        }

        [Fact]
        public void OneWayPatch_OnlyMemberOneCanStartAForward()
        {
            var codeplug = MakeCodeplug(passthrough: false);
            var sender = new RecordingSender();
            using var coordinator = CreateCoordinator(codeplug, new RecordingEncoder(), sender);
            coordinator.ApplySavedMemberships(MakeSection(
                patchMembers: ("S1", "101", "S2", "202"),
                patchEnabled: true,
                oneWay: true));

            coordinator.HandleReceiveFrame(Metadata("S2", 42, 202, 9, VoiceMode.Dmr));
            coordinator.ObserveDecodedPcm("s2|202|slot:0", VoiceMode.Dmr, Pcm());
            coordinator.ObserveDecodedPcm("s2|202|slot:0", VoiceMode.Dmr, Pcm());
            coordinator.ObserveDecodedPcm("s2|202|slot:0", VoiceMode.Dmr, Pcm());

            Assert.Empty(sender.DmrVoiceCalls);
            Assert.Empty(sender.DmrTerminatorCalls);
        }

        [Fact]
        public void MultiSelectMembership_IsNotAppliedToPatchManager()
        {
            var codeplug = MakeCodeplug(passthrough: false);
            codeplug.Groups.Add(new Codeplug.Group { Name = "Multi", Type = "multiselect" });
            var sender = new RecordingSender();
            using var coordinator = CreateCoordinator(codeplug, new RecordingEncoder(), sender);
            coordinator.ApplySavedMemberships(MakeSection(
                patchMembers: ("S1", "101", "S2", "202"),
                patchEnabled: false,
                oneWay: false,
                includeMultiSelect: true));

            coordinator.HandleReceiveFrame(Metadata("S1", 42, 101, 7, VoiceMode.Dmr));
            coordinator.ObserveDecodedPcm("s1|101|slot:0", VoiceMode.Dmr, Pcm());
            coordinator.ObserveDecodedPcm("s1|101|slot:0", VoiceMode.Dmr, Pcm());
            coordinator.ObserveDecodedPcm("s1|101|slot:0", VoiceMode.Dmr, Pcm());

            Assert.Empty(sender.DmrVoiceCalls);
        }

        [Fact]
        public void MissingOrDisconnectedTarget_ReturnsZeroAndSendsNothing()
        {
            var codeplug = MakeCodeplug(passthrough: false);
            var sender = new RecordingSender();
            using var coordinator = CreateCoordinator(
                codeplug,
                new RecordingEncoder(),
                sender,
                isSystemConnected: system => !string.Equals(system, "S2", StringComparison.OrdinalIgnoreCase));
            coordinator.ApplySavedMemberships(MakeSection(
                patchMembers: ("S1", "101", "S2", "202"),
                patchEnabled: true,
                oneWay: false));

            coordinator.HandleReceiveFrame(Metadata("S1", 42, 101, 7, VoiceMode.Dmr));
            coordinator.ObserveDecodedPcm("s1|101|slot:0", VoiceMode.Dmr, Pcm());
            coordinator.ObserveDecodedPcm("s1|101|slot:0", VoiceMode.Dmr, Pcm());
            coordinator.ObserveDecodedPcm("s1|101|slot:0", VoiceMode.Dmr, Pcm());

            Assert.Empty(sender.DmrVoiceCalls);
            Assert.Empty(sender.DmrTerminatorCalls);
        }

        [Fact]
        public void SourceIdPassthrough_UsesInboundSourceId()
        {
            var codeplug = MakeCodeplug(passthrough: true);
            var sender = new RecordingSender();
            using var coordinator = CreateCoordinator(codeplug, new RecordingEncoder(), sender);
            coordinator.ApplySavedMemberships(MakeSection(
                patchMembers: ("S1", "101", "S2", "202"),
                patchEnabled: true,
                oneWay: false));

            coordinator.HandleReceiveFrame(Metadata("S1", 777, 101, 7, VoiceMode.Dmr));
            coordinator.ObserveDecodedPcm("s1|101|slot:0", VoiceMode.Dmr, Pcm());
            coordinator.ObserveDecodedPcm("s1|101|slot:0", VoiceMode.Dmr, Pcm());
            coordinator.ObserveDecodedPcm("s1|101|slot:0", VoiceMode.Dmr, Pcm());

            Assert.Equal(777u, Assert.Single(sender.DmrVoiceCalls).Target.SourceId);
        }

        [Fact]
        public void ReceiveTgidAndForwardTeardown_PreserveCodeplugSpellingAndCaseInsensitiveSystem()
        {
            var codeplug = MakeCodeplug(passthrough: false);
            codeplug.Zones[0].Channels[0].Tgid = "0101";
            codeplug.Zones[0].Channels[1].Tgid = "0202";
            var sender = new RecordingSender();
            using var coordinator = CreateCoordinator(codeplug, new RecordingEncoder(), sender);
            coordinator.ApplySavedMemberships(MakeSection(
                patchMembers: ("s1", "0101", "s2", "0202"),
                patchEnabled: true,
                oneWay: false));

            coordinator.HandleReceiveFrame(Metadata("S1", 42, 101, 7, VoiceMode.Dmr));
            coordinator.ObserveDecodedPcm("s1|101|slot:0", VoiceMode.Dmr, Pcm());
            coordinator.ObserveDecodedPcm("s1|101|slot:0", VoiceMode.Dmr, Pcm());
            coordinator.ObserveDecodedPcm("s1|101|slot:0", VoiceMode.Dmr, Pcm());
            coordinator.HandleStreamEnded("s1|101|slot:0", VoiceMode.Dmr);

            Assert.Single(sender.DmrVoiceCalls);
            Assert.Equal("0202", sender.DmrVoiceCalls[0].Target.TalkgroupId);
            Assert.Single(sender.DmrTerminatorCalls);
        }

        [Fact]
        public void P25_GrantVoiceAndEndUseIndependentForwardState()
        {
            var codeplug = MakeCodeplug(passthrough: false, targetMode: "p25");
            var sender = new RecordingSender();
            using var coordinator = CreateCoordinator(codeplug, new RecordingEncoder(), sender);
            coordinator.ApplySavedMemberships(MakeSection(
                patchMembers: ("S1", "101", "S2", "202"),
                patchEnabled: true,
                oneWay: false));

            coordinator.HandleReceiveFrame(Metadata("S1", 42, 101, 8, VoiceMode.P25));
            Assert.Equal(1, sender.P25TduCalls.Count(call => call.GrantDemand));

            for (var i = 0; i < 9; i++)
            {
                coordinator.ObserveDecodedPcm("s1|101", VoiceMode.P25, Pcm());
            }

            var ldu = Assert.Single(sender.P25LduCalls);
            Assert.Equal("202", ldu.Target.TalkgroupId);
            Assert.Equal(225, ldu.Payload.Length);
            Assert.Equal(1u, ldu.StreamId);

            coordinator.HandleStreamEnded("s1|101", VoiceMode.P25);
            var end = Assert.Single(sender.P25TduCalls.Where(call => !call.GrantDemand));
            Assert.Equal(ldu.StreamId, end.StreamId);
        }

        [Fact]
        public void LatePatchedTransmitStream_IsSuppressedByCoreLifecycle()
        {
            var codeplug = MakeCodeplug(passthrough: false);
            var sender = new RecordingSender();
            using var coordinator = CreateCoordinator(codeplug, new RecordingEncoder(), sender);
            coordinator.ApplySavedMemberships(MakeSection(
                patchMembers: ("S1", "101", "S2", "202"),
                patchEnabled: true,
                oneWay: false));

            coordinator.HandleReceiveFrame(Metadata("S1", 42, 101, 7, VoiceMode.Dmr));
            coordinator.ObserveDecodedPcm("s1|101|slot:0", VoiceMode.Dmr, Pcm());
            coordinator.ObserveDecodedPcm("s1|101|slot:0", VoiceMode.Dmr, Pcm());
            coordinator.ObserveDecodedPcm("s1|101|slot:0", VoiceMode.Dmr, Pcm());
            coordinator.HandleStreamEnded("s1|101|slot:0", VoiceMode.Dmr);

            var voicesBeforeLateFrame = sender.DmrVoiceCalls.Count;
            coordinator.HandleReceiveFrame(Metadata("S2", 2002, 202, 1, VoiceMode.Dmr));
            coordinator.ObserveDecodedPcm("s2|202|slot:0", VoiceMode.Dmr, Pcm());
            coordinator.ObserveDecodedPcm("s2|202|slot:0", VoiceMode.Dmr, Pcm());
            coordinator.ObserveDecodedPcm("s2|202|slot:0", VoiceMode.Dmr, Pcm());

            Assert.Equal(voicesBeforeLateFrame, sender.DmrVoiceCalls.Count);
        }

        [Fact]
        public void Dispose_EndsActiveForwards_WithoutAPlaybackDependency()
        {
            var codeplug = MakeCodeplug(passthrough: false);
            var sender = new RecordingSender();
            var coordinator = CreateCoordinator(codeplug, new RecordingEncoder(), sender);
            coordinator.ApplySavedMemberships(MakeSection(
                patchMembers: ("S1", "101", "S2", "202"),
                patchEnabled: true,
                oneWay: false));
            coordinator.HandleReceiveFrame(Metadata("S1", 42, 101, 7, VoiceMode.Dmr));

            coordinator.Dispose();
            coordinator.Dispose();

            Assert.Single(sender.DmrTerminatorCalls);
        }

        [Fact]
        public void MainWindowSource_ComposesEveryReceiveForwardingBoundary()
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            var source = File.ReadAllText(Path.Combine(root, "DvmConsole.Avalonia", "MainWindow.axaml.cs"));

            Assert.Contains("new PatchForwardingCoordinator", source);
            Assert.Contains("patchForwardingCoordinator?.HandleReceiveFrame(metadata)", source);
            Assert.Contains("decodedPcmObserver: (IDecodedPcmObserver?)patchForwardingCoordinator", source);
            Assert.Contains("patchForwardingCoordinator?.ApplySavedMemberships(section)", source);
            Assert.Contains("patchForwardingCoordinator?.HandleStreamEnded(key, mode)", source);
            Assert.Contains("patchForwardingCoordinator?.Dispose()", source);
        }

        private static PatchForwardingCoordinator CreateCoordinator(
            Codeplug codeplug,
            IVoiceFrameEncoder encoder,
            IVoiceTrafficSender sender,
            Func<string, bool>? isSystemConnected = null)
            => new PatchForwardingCoordinator(
                codeplug,
                encoder,
                sender,
                Context,
                downstreamObserver: null,
                isSystemConnected: isSystemConnected,
                utcNow: () => DateTime.UtcNow);

        private static ReceivedCallMetadata Metadata(
            string system,
            uint source,
            uint destination,
            uint streamId,
            VoiceMode mode,
            bool terminator = false)
            => new ReceivedCallMetadata(
                system,
                source,
                destination,
                0,
                mode,
                streamId,
                mode == VoiceMode.P25
                    ? $"{system.ToLowerInvariant()}|{destination}"
                    : $"{system.ToLowerInvariant()}|{destination}|slot:0",
                terminator);

        private static ReadOnlyMemory<byte> Pcm() => new byte[320];

        private static Codeplug MakeCodeplug(bool passthrough, string targetMode = "dmr")
            => new Codeplug
            {
                PatchSourceIdPassthrough = passthrough,
                Systems = new List<Codeplug.System>
                {
                    new Codeplug.System { Name = "S1", Rid = "1001" },
                    new Codeplug.System { Name = "S2", Rid = "2002" },
                },
                Zones = new List<Codeplug.Zone>
                {
                    new Codeplug.Zone
                    {
                        Name = "Zone",
                        Channels = new List<Codeplug.Channel>
                        {
                            new Codeplug.Channel { Name = "Source", System = "S1", Tgid = "101", Slot = 1, Mode = "dmr" },
                            new Codeplug.Channel { Name = "Target", System = "S2", Tgid = "202", Slot = 2, Mode = targetMode },
                        },
                    },
                },
                Groups = new List<Codeplug.Group>
                {
                    new Codeplug.Group { Name = "Patch", Type = "patch" },
                },
            };

        private static UserSettingsGroupSection MakeSection(
            (string SourceSystem, string SourceTgid, string TargetSystem, string TargetTgid) patchMembers,
            bool patchEnabled,
            bool oneWay,
            bool includeMultiSelect = false)
        {
            var memberships = new Dictionary<string, List<PatchTalkgroupMember>>
            {
                ["Patch"] = new List<PatchTalkgroupMember>
                {
                    new PatchTalkgroupMember { SystemName = patchMembers.SourceSystem, Tgid = patchMembers.SourceTgid },
                    new PatchTalkgroupMember { SystemName = patchMembers.TargetSystem, Tgid = patchMembers.TargetTgid },
                },
            };
            if (includeMultiSelect)
            {
                memberships["Multi"] = new List<PatchTalkgroupMember>
                {
                    new PatchTalkgroupMember { SystemName = "S1", Tgid = "101" },
                    new PatchTalkgroupMember { SystemName = "S2", Tgid = "202" },
                };
            }

            return new UserSettingsGroupSection
            {
                PatchGroupMemberships = new Dictionary<string, Dictionary<string, List<PatchTalkgroupMember>>>
                {
                    [Context] = memberships,
                },
                PatchGroupModes = new Dictionary<string, Dictionary<string, bool>>
                {
                    [Context] = new Dictionary<string, bool> { ["Patch"] = oneWay },
                },
                PatchGroupEnabledStates = new Dictionary<string, Dictionary<string, bool>>
                {
                    [Context] = new Dictionary<string, bool> { ["Patch"] = patchEnabled },
                },
            };
        }

        private sealed class RecordingEncoder : IVoiceFrameEncoder
        {
            public bool TryEncode(VoiceMode mode, ReadOnlyMemory<short> samples, out byte[] codeword)
            {
                codeword = Enumerable.Repeat((byte)(mode == VoiceMode.Dmr ? 0xD : 0xE), mode == VoiceMode.Dmr ? 9 : 11).ToArray();
                return samples.Length == 160;
            }
        }

        private sealed class RecordingSender : IVoiceTrafficSender
        {
            public List<DmrVoiceCall> DmrVoiceCalls { get; } = new List<DmrVoiceCall>();
            public List<P25LduCall> P25LduCalls { get; } = new List<P25LduCall>();
            public List<DmrTerminatorCall> DmrTerminatorCalls { get; } = new List<DmrTerminatorCall>();
            public List<P25TduCall> P25TduCalls { get; } = new List<P25TduCall>();

            public void SendDmrVoice(TransmitTarget target, ReadOnlyMemory<byte> ambe27, uint streamId, int seqNo)
                => DmrVoiceCalls.Add(new DmrVoiceCall(target, ambe27.ToArray(), streamId, seqNo));

            public void SendP25Ldu(TransmitTarget target, bool isLdu2, ReadOnlyMemory<byte> ldu225, uint streamId, int seqNo)
                => P25LduCalls.Add(new P25LduCall(target, ldu225.ToArray(), streamId, seqNo, isLdu2));

            public void SendDmrTerminator(TransmitTarget target, uint streamId, int nextSeqNo)
                => DmrTerminatorCalls.Add(new DmrTerminatorCall(target, streamId, nextSeqNo));

            public void SendP25Tdu(TransmitTarget target, uint streamId, bool grantDemand)
                => P25TduCalls.Add(new P25TduCall(target, streamId, grantDemand));
        }

        private sealed record DmrVoiceCall(TransmitTarget Target, byte[] Payload, uint StreamId, int SeqNo);
        private sealed record P25LduCall(TransmitTarget Target, byte[] Payload, uint StreamId, int SeqNo, bool IsLdu2);
        private sealed record DmrTerminatorCall(TransmitTarget Target, uint StreamId, int NextSeqNo);
        private sealed record P25TduCall(TransmitTarget Target, uint StreamId, bool GrantDemand);
    }
}
