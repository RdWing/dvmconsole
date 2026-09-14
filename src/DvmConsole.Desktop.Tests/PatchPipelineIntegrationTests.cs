// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Concurrent;
using DvmConsole.Application;
using DvmConsole.Core.Configuration;
using DvmConsole.Core.Runtime;
using DvmConsole.Desktop;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Vocoder;
using DvmConsole.Audio;
using DvmConsole.Storage;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class PatchPipelineIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComposedSessionAppliesOneWayPatchAndRecoversOnlyNewTraffic(bool sourceIdPassthrough)
    {
        var (source, sourceRadio) = CreateChannel("Source", "Source channel", 100, 1001);
        var (target, targetRadio) = CreateChannel("Target", "Target channel", 200, 2001, "dmr");
        string root = Path.Combine(Path.GetTempPath(), "neo-composed-patch-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new ManagedReceivePreferences(Path.Combine(root, "UserSettings.json"));
            var configuration = new ConsoleConfiguration
            {
                PatchSourceIdPassthrough = sourceIdPassthrough,
                Systems = new[] { "Source", "Target" }.Select((name, index) => new SystemConfiguration
                { Name = name, Identity = "Test", Address = "127.0.0.1", Port = 62031, PeerId = (uint)(index + 1), Rid = "1001" }).ToList(),
                Groups = [new() { Name = "Patch" }],
                Zones = [new() { Name = "Test", Channels = [
                    new() { Name = "Source channel", System = "Source", Mode = "p25", Tgid = "100" },
                    new() { Name = "Target channel", System = "Target", Mode = "dmr", Tgid = "200", Slot = 1 }] }]
            };
            var factory = new SessionFactory(sourceRadio, targetRadio);
            var systems = configuration.Systems.Select(system => new RadioSystemDescriptor(SystemId.FromName(system.Name),
                system.Name, "FNE", new Dictionary<string, string>())).ToArray();
            await using var session = await ConsoleReceiveSession.CreateAsync(configuration, (state, _) =>
            {
                var host = new ConsoleHostServices(factory, null!, new NativeFactory(), null!, null!, null!, new SessionLifecycle(),
                    SystemClock.Instance, new BackgroundApplicationScheduler(_ => { }), SystemApplicationDelay.Instance, null!, []);
                return new(host, systems, DvmConsole.FneIntegration.FneReceiveFrameNormalization.Instance,
                    Preferences: settings.ForConfiguration(ConfigurationId.New(), state.Channels.ToDictionary(pair => pair.Key,
                        pair => pair.Value.Runtime.Definition.SystemName + "\u001F" + pair.Value.Runtime.Definition.Name)),
                    ManualInput: new AudioInputProcessingOptions());
            });
            await session.SaveGroupAsync("Patch", [source.Id, target.Id], true, true);
            Assert.Contains("Patch", session.EnabledPatchGroups);
            Assert.True(Assert.Single(session.CaptureSnapshot().Channels[source.Id].Patches).IsSource);
            short[] samples = CreateTestAudio(P25DfsiFrameCodec.CodewordsPerLdu);
            sourceRadio.Emit(CreateNativeP25Voice(source, samples, 7001, 77));
            await WaitForSentCountAsync(targetRadio, 4);
            Assert.All(targetRadio.Sent, packet => Assert.Equal(sourceIdPassthrough ? 7001u : 2001u,
                (uint)(packet.Payload[5] << 16 | packet.Payload[6] << 8 | packet.Payload[7])));
            Assert.Empty(sourceRadio.Sent);
            session.SetAudioAvailable(false, "Test interruption");
            await session.ResumeAudioAsync(_ => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
            targetRadio.ClearSent();
            sourceRadio.Emit(CreateNativeP25Voice(source, samples, 7001, 77));
            sourceRadio.Emit(CreateNativeP25Voice(source, samples, 7001, 78));
            await WaitForSentCountAsync(targetRadio, 4);
            Assert.Single(targetRadio.Sent.Select(packet => packet.StreamId).Distinct());
            await session.QuiesceAsync(CancellationToken.None);
            session.ReactivateAfterFailedReplacement();
            await session.RestoreAsync([sourceRadio.SystemId, targetRadio.SystemId]);
            targetRadio.ClearSent();
            sourceRadio.Emit(CreateNativeP25Voice(source, samples, 7001, 79));
            await WaitForSentCountAsync(targetRadio, 4);
            await session.SaveGroupAsync("Patch", [source.Id, target.Id], false, true);
            Assert.Single(targetRadio.Sent.Select(packet => packet.StreamId).Distinct());
            Assert.DoesNotContain("Patch", session.EnabledPatchGroups);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class SessionFactory(params FakeEndpoint[] radios) : IRadioSessionFactory
    {
        public ValueTask<IRadioSession> CreateAsync(RadioSystemDescriptor system, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IRadioSession>(radios.Single(radio => radio.Name == system.Name));
    }
    private sealed class NativeFactory : IVocoderFactory
    {
        public IVocoderBackend Create(IReadOnlyDictionary<VocoderMode, ReceiveAudioProcessingOptions>? receiveAudioProcessingOptions = null)
            => new SoftwareVocoderBackend();
    }
    private sealed class SessionLifecycle : IApplicationLifecycle
    {
        public bool IsActive => true;
        public event EventHandler? Activated { add { } remove { } }
        public event EventHandler? Deactivated { add { } remove { } }
        public event EventHandler? Suspending { add { } remove { } }
        public event EventHandler? Resumed { add { } remove { } }
        public event EventHandler? Stopping { add { } remove { } }
    }
    [Fact]
    public async Task PatchRecoveryRejectsOldQueueEpochAndCurrentInterruptedStream()
    {
        var (source, sourceSystem) = CreateChannel("Source", "Source channel", 100, 1001);
        var (target, targetSystem) = CreateChannel("Target", "Target channel", 200, 2001);
        var runtime = new ConsolePatchRuntime();
        await using var services = new ConsoleSessionServices();
        runtime.RegisterOwnership(services);
        int starts = 0;
        runtime.Initialize([sourceSystem, targetSystem], new TransmitKeyPort(null, null, null),
            () => { starts++; return new FakeVocoderBackend(); }, () => new FakeVocoderBackend(),
            id => id == source.Id ? source.ToTransmitDescriptor() : target.ToTransmitDescriptor(),
            () => DmrReceiveKeyPolicy.OnAirMetadata, false, _ => { }, (_, _) => default, _ => { }, _ => { });
        await runtime.Decoder.ApplyChannelsAsync([source]);
        runtime.Forwarding.ApplyMemberships(new Dictionary<string, IReadOnlyList<PatchMemberAddress>>
        { ["Patch"] = [new("Source", 100), new("Target", 200)] });
        await runtime.PauseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(runtime.Enqueue(source.Id, CreateVoice(source, 42, 77), null));
        runtime.Resume([new(source.Id, 77)]);
        runtime.Work.Start(source.Id);
        // Simulate late delivery of a frame admitted before the interruption.
        runtime.Work.Enqueue(source.Id, RadioMediaIngressFrame.FromFrame(CreateVoice(source, 42, 999)));
        await runtime.Work.RunAfterStreamsAsync(source.Id, [999], () => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, starts);
        Assert.False(runtime.Enqueue(source.Id, CreateVoice(source, 42, 77), null));
        Assert.True(runtime.Enqueue(source.Id, CreateVoice(source, 42, 78), null));
        await runtime.Work.RunAfterStreamsAsync(source.Id, [78], () => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialPatchConstructionRetiresForwardingEvenWhenPresentationCleanupFails(bool failCleanup)
    {
        var (source, sourceSystem) = CreateChannel("Source", "Source channel", 100, 1001);
        var (target, targetSystem) = CreateChannel("Target", "Target channel", 200, 2001);
        var runtime = new ConsolePatchRuntime();
        var services = new ConsoleSessionServices();
        int transmitBackends = 0;
        int detachments = 0;
        runtime.RegisterOwnership(services, () =>
        {
            detachments++;
            if (failCleanup) throw new InvalidOperationException("Presentation cleanup fixture failure.");
        });
        // Fail after forwarding construction but before a decoder can be owned.
        Assert.Throws<ArgumentNullException>(() => runtime.Initialize(
            [sourceSystem, targetSystem], new TransmitKeyPort(null, null, null),
            () => { transmitBackends++; return new FakeVocoderBackend(); }, null!,
            id => id == source.Id ? source.ToTransmitDescriptor() : target.ToTransmitDescriptor(),
            () => DmrReceiveKeyPolicy.OnAirMetadata, false, _ => { }, (_, _) => default,
            _ => { }, _ => { }));
        Assert.NotNull(runtime.Forwarding);
        Assert.Null(runtime.Decoder);
        if (failCleanup)
            await Assert.ThrowsAsync<InvalidOperationException>(() => services.DisposeAsync().AsTask());
        else
            await services.DisposeAsync();

        runtime.Forwarding.ApplyMemberships(new Dictionary<string, IReadOnlyList<PatchMemberAddress>>
        {
            ["Patch"] = [new("Source", 100), new("Target", 200)]
        });
        runtime.Forwarding.ObserveTraffic(source.Id, CreateVoice(source, 42, 77));
        Assert.Equal(0, transmitBackends);
        Assert.Empty(targetSystem.Sent);
        Assert.Equal(1, detachments);
    }

    [Fact]
    public async Task CancelledSourceWorkCannotStartAPatchTransmitter()
    {
        var (source, sourceSystem) = CreateChannel("Source", "Source channel", 100, 1001);
        var (target, targetSystem) = CreateChannel("Target", "Target channel", 200, 2001);
        int transmitBackends = 0;
        await using var forwarding = new PatchForwardingCoordinator([sourceSystem, targetSystem],
            createVocoderBackend: () => { transmitBackends++; return new FakeVocoderBackend(); });
        await using var decoding = new PatchSourceDecodeCoordinator(null,
            forwarding.ObserveDecodedSamples, () => new FakeVocoderBackend());
        await decoding.ApplyChannelsAsync([source, target]);
        forwarding.ApplyMemberships(new Dictionary<string, IReadOnlyList<PatchMemberAddress>>
        {
            ["Patch"] = [new("Source", 100), new("Target", 200)]
        });
        var pipeline = new PatchSourceReceivePipeline(decoding, forwarding);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pipeline.ProcessAsync(source, CreateVoice(source, 42, 77), cancellation.Token));
        Assert.Equal(0, transmitBackends);
        Assert.Empty(targetSystem.Sent);

        await pipeline.ProcessAsync(source, CreateVoice(source, 42, 78));
        Assert.Equal(1, transmitBackends);
    }

    [Fact]
    public async Task MixedModePatchTranscodesNativeAudioInBothDirections()
    {
        (ChannelViewModel p25, FakeEndpoint p25System) = CreateChannel(
            "P25 FNE",
            "P25 Dispatch",
            destinationId: 747,
            sourceId: 3_222_223,
            mode: "p25");
        (ChannelViewModel dmr, FakeEndpoint dmrSystem) = CreateChannel(
            "DMR FNE",
            "DMR Dispatch",
            destinationId: 99,
            sourceId: 890,
            mode: "dmr",
            slot: 1);
        using var forwarding = new PatchForwardingCoordinator(
            [p25System, dmrSystem],
            createVocoderBackend: () => new SoftwareVocoderBackend());
        await using var decoding = new PatchSourceDecodeCoordinator(
            null,
            (channel, streamId, sourceId, samples) =>
                forwarding.ObserveDecodedSamples(channel, streamId, sourceId, samples),
            () => new SoftwareVocoderBackend());
        forwarding.ApplyMemberships(new Dictionary<string, IReadOnlyList<PatchMemberAddress>>
        {
            ["Mixed Mode"] =
            [
                new(p25.Definition.SystemName, p25.Definition.DestinationId),
                new(dmr.Definition.SystemName, dmr.Definition.DestinationId)
            ]
        });
        await decoding.ApplyChannelsAsync([p25, dmr]);

        short[] sourceAudio = CreateTestAudio(P25DfsiFrameCodec.CodewordsPerLdu);
        FneTrafficFrame p25Voice = CreateNativeP25Voice(p25, sourceAudio, sourceId: 7_471, streamId: 101);
        forwarding.ObserveTraffic(p25, p25Voice);
        Assert.Equal(0, await decoding.ProcessAsync(p25, p25Voice));

        await WaitForSentCountAsync(dmrSystem, 4);
        SentPacket[] dmrVoicePackets = dmrSystem.Sent.Skip(1).ToArray();
        Assert.Equal(3, dmrVoicePackets.Length);
        Assert.All(dmrVoicePackets, packet => Assert.Equal(FneTrafficProtocol.Dmr, packet.Protocol));
        AssertNativeDmrAudio(dmrVoicePackets);

        forwarding.StopSource(p25, 101);
        dmrSystem.ClearSent();
        p25System.ClearSent();

        IReadOnlyList<FneTrafficFrame> dmrVoice = CreateNativeDmrVoice(
            dmr,
            sourceAudio,
            sourceId: 8_901,
            streamId: 202);
        foreach (FneTrafficFrame voice in dmrVoice)
        {
            forwarding.ObserveTraffic(dmr, voice);
            Assert.Equal(0, await decoding.ProcessAsync(dmr, voice));
        }

        await WaitForSentCountAsync(p25System, 2);
        SentPacket p25Ldu = Assert.Single(p25System.Sent, packet =>
            packet.Protocol == FneTrafficProtocol.P25 &&
            packet.Payload[22] is P25DfsiFrameCodec.Ldu1Duid or P25DfsiFrameCodec.Ldu2Duid);
        AssertNativeP25Audio(p25Ldu);
    }

    [Fact]
    public async Task ExactMemberIdentitySelectsDmrWhenSameFneAndTalkgroupAlsoHaveP25()
    {
        (ChannelViewModel source, FakeEndpoint sourceSystem) = CreateChannel(
            "Source FNE",
            "P25 Source",
            destinationId: 747,
            sourceId: 3_222_223,
            mode: "p25");
        var p25Collision = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "P25 99",
            System = "Destination FNE",
            Tgid = "99",
            Mode = "p25"
        });
        var dmrTarget = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "DMR 99",
            System = "Destination FNE",
            Tgid = "99",
            Mode = "dmr",
            Slot = 1
        });
        var destinationSystem = new FakeEndpoint(
            "Destination FNE",
            [p25Collision, dmrTarget],
            sourceId: 890);
        using var forwarding = new PatchForwardingCoordinator(
            [sourceSystem, destinationSystem],
            createVocoderBackend: () => new SoftwareVocoderBackend());
        await using var decoding = new PatchSourceDecodeCoordinator(
            null,
            (channel, streamId, sourceId, samples) =>
                forwarding.ObserveDecodedSamples(channel, streamId, sourceId, samples),
            () => new SoftwareVocoderBackend());
        forwarding.ApplyMemberships(new Dictionary<string, IReadOnlyList<PatchMemberAddress>>
        {
            ["Cross Mode"] =
            [
                new(source.Definition.SystemName, source.Definition.DestinationId, source.Name),
                new(dmrTarget.Definition.SystemName, dmrTarget.Definition.DestinationId, dmrTarget.Name)
            ]
        });
        await decoding.ApplyChannelsAsync([source, dmrTarget]);

        short[] sourceAudio = CreateTestAudio(P25DfsiFrameCodec.CodewordsPerLdu);
        FneTrafficFrame voice = CreateNativeP25Voice(source, sourceAudio, sourceId: 7_471, streamId: 303);
        forwarding.ObserveTraffic(source, voice);
        Assert.Equal(0, await decoding.ProcessAsync(source, voice));

        await WaitForSentCountAsync(destinationSystem, 4);
        Assert.All(destinationSystem.Sent, packet => Assert.Equal(FneTrafficProtocol.Dmr, packet.Protocol));
        AssertNativeDmrAudio(destinationSystem.Sent.Skip(1));
    }

    [Fact]
    public async Task TwoMemberP25PatchForwardsCompleteCallsInBothDirections()
    {
        (ChannelViewModel first, FakeEndpoint firstSystem) = CreateChannel(
            "TYF",
            "747 Select P25",
            destinationId: 747,
            sourceId: 3_222_223);
        (ChannelViewModel second, FakeEndpoint secondSystem) = CreateChannel(
            "TEST FNE",
            "PARROT P25",
            destinationId: 9_990,
            sourceId: 890);
        using var forwarding = new PatchForwardingCoordinator(
            [firstSystem, secondSystem],
            createVocoderBackend: () => new FakeVocoderBackend());
        await using var decoding = new PatchSourceDecodeCoordinator(
            null,
            (channel, streamId, sourceId, samples) =>
                forwarding.ObserveDecodedSamples(channel, streamId, sourceId, samples),
            () => new FakeVocoderBackend());
        var receivePipeline = new PatchSourceReceivePipeline(decoding, forwarding);
        forwarding.ApplyMemberships(new Dictionary<string, IReadOnlyList<PatchMemberAddress>>
        {
            ["Patch Test 1"] =
            [
                new(first.Definition.SystemName, first.Definition.DestinationId),
                new(second.Definition.SystemName, second.Definition.DestinationId)
            ]
        });
        await decoding.ApplyChannelsAsync([first, second]);

        await ForwardCallAsync(
            first,
            firstSystem,
            decoding,
            receivePipeline,
            sourceId: 7_471,
            streamId: 101);

        await WaitForSentCountAsync(secondSystem, 3);
        AssertP25Call(secondSystem, expectedDestinationId: 9_990);

        await ForwardCallAsync(
            second,
            secondSystem,
            decoding,
            receivePipeline,
            sourceId: 9_901,
            streamId: 202);

        await WaitForSentCountAsync(firstSystem, 3);
        AssertP25Call(firstSystem, expectedDestinationId: 747);
    }

    private static async Task ForwardCallAsync(
        ChannelViewModel source,
        FakeEndpoint sourceSystem,
        PatchSourceDecodeCoordinator decoding,
        PatchSourceReceivePipeline receivePipeline,
        uint sourceId,
        uint streamId)
    {
        var ReceiveAudioTrafficRouter = new ReceiveAudioTrafficRouterTestHarness();
        IReadOnlyDictionary<(FneTrafficProtocol, uint), ChannelViewModel[]> routes =
            new Dictionary<(FneTrafficProtocol, uint), ChannelViewModel[]>
            {
                [(FneTrafficProtocol.P25, source.Definition.DestinationId)] = [source]
            };
        FneTrafficFrame voice = CreateVoice(source, sourceId, streamId);
        ReceiveIngressRoutingDecision ingress = ReceiveAudioTrafficRouter.ObserveIngress(
            routes,
            voice,
            (channel, candidateStreamId) => decoding.IsTrackingStream(channel, candidateStreamId));
        ChannelViewModel[] activeChannels = routes.Values
            .SelectMany(channels => channels)
            .Where(channel => decoding.ActiveChannels.Contains((ChannelId)channel))
            .Distinct()
            .ToArray();
        ChannelViewModel target = Assert.Single(ReceiveAudioTrafficRouter.ResolveTargets(
            routes,
            activeChannels,
            voice,
            ingress,
            (channel, candidateStreamId) => decoding.IsTrackingStream(channel, candidateStreamId)));

        Assert.Equal(0, await receivePipeline.ProcessAsync(target, voice));

        FneTrafficFrame terminator = CreateTerminator(source, sourceId, streamId);
        ReceiveIngressRoutingDecision terminatorIngress = ReceiveAudioTrafficRouter.ObserveIngress(
            routes,
            terminator,
            (channel, candidateStreamId) => decoding.IsTrackingStream(channel, candidateStreamId));
        ChannelViewModel terminatorTarget = Assert.Single(ReceiveAudioTrafficRouter.ResolveTargets(
            routes,
            activeChannels,
            terminator,
            terminatorIngress,
            (channel, candidateStreamId) => decoding.IsTrackingStream(channel, candidateStreamId)));
        Assert.Same(target, terminatorTarget);
        await receivePipeline.ProcessAsync(terminatorTarget, terminator);

        Assert.DoesNotContain(sourceSystem.Sent, packet => packet.StreamId == streamId);
    }

    private static FneTrafficFrame CreateVoice(
        ChannelViewModel channel,
        uint sourceId,
        uint streamId)
        => new(
            FneTrafficProtocol.P25,
            peerId: 1,
            sourceId,
            channel.Definition.DestinationId,
            slot: null,
            callType: "GROUP",
            frameType: "VOICE",
            subtype: "LDU1",
            packetSequence: 0,
            streamId,
            P25DfsiFrameCodec.CreateLdu1Payload(
                sourceId,
                channel.Definition.DestinationId,
                new byte[P25DfsiFrameCodec.ImbeBytes]));

    private static FneTrafficFrame CreateTerminator(
        ChannelViewModel channel,
        uint sourceId,
        uint streamId)
        => new(
            FneTrafficProtocol.P25,
            peerId: 1,
            sourceId,
            channel.Definition.DestinationId,
            slot: null,
            callType: "GROUP",
            frameType: "TERMINATOR",
            subtype: "TDU",
            packetSequence: P25DfsiFrameCodec.RtpCallEndSequence,
            streamId,
            P25DfsiFrameCodec.CreateTduPayload(
                sourceId,
                channel.Definition.DestinationId,
                grantDemand: false));

    private static void AssertP25Call(FakeEndpoint endpoint, uint expectedDestinationId)
    {
        Assert.True(endpoint.Sent.Count >= 3);
        Assert.All(endpoint.Sent, packet => Assert.Equal(FneTrafficProtocol.P25, packet.Protocol));
        Assert.Equal(P25DfsiFrameCodec.TduDuid, endpoint.Sent[0].Payload[22]);
        Assert.Contains(endpoint.Sent, packet =>
            packet.Payload[22] == P25DfsiFrameCodec.Ldu1Duid &&
            packet.StreamId != 0);
        SentPacket ldu = endpoint.Sent.First(packet =>
            packet.Payload[22] == P25DfsiFrameCodec.Ldu1Duid);
        Assert.True(P25DfsiFrameCodec.TryExtractCallIdentifiers(
            new FneTrafficFrame(
                FneTrafficProtocol.P25,
                peerId: 1,
                sourceId: 0,
                destinationId: 0,
                slot: null,
                callType: "GROUP",
                frameType: "VOICE",
                subtype: "LDU1",
                packetSequence: ldu.PacketSequence,
                streamId: ldu.StreamId,
                ldu.Payload),
            out _,
            out uint destinationId));
        Assert.Equal(expectedDestinationId, destinationId);
        Assert.Equal(P25DfsiFrameCodec.TduDuid, endpoint.Sent[^1].Payload[22]);
    }

    private static (ChannelViewModel Channel, FakeEndpoint System) CreateChannel(
        string systemName,
        string channelName,
        uint destinationId,
        uint sourceId,
        string mode = "p25",
        byte slot = 1)
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = channelName,
            System = systemName,
            Tgid = destinationId.ToString(),
            Mode = mode,
            Slot = slot
        });
        return (channel, new FakeEndpoint(systemName, [channel], sourceId));
    }

    private static short[] CreateTestAudio(int frameCount)
    {
        var samples = new short[frameCount * VocoderFrameSizes.PcmSamplesPerFrame];
        for (int index = 0; index < samples.Length; index++)
        {
            double time = index / 8_000d;
            double envelope = 0.65 + 0.35 * Math.Sin(2 * Math.PI * 3 * time);
            samples[index] = (short)(envelope * (
                10_000 * Math.Sin(2 * Math.PI * 220 * time) +
                4_000 * Math.Sin(2 * Math.PI * 660 * time)));
        }
        return samples;
    }

    private static FneTrafficFrame CreateNativeP25Voice(
        ChannelViewModel channel,
        ReadOnlySpan<short> samples,
        uint sourceId,
        uint streamId)
    {
        var packets = new List<SentPacket>();
        using var backend = new SoftwareVocoderBackend();
        using IVocoderSession vocoder = backend.CreateSession(VocoderMode.P25Imbe);
        using var encoder = new P25TxAudioSession(
            sourceId,
            channel.Definition.DestinationId,
            streamId,
            vocoder,
            (payload, sequence, outboundStreamId) => packets.Add(new SentPacket(
                FneTrafficProtocol.P25,
                payload.ToArray(),
                sequence,
                outboundStreamId)));

        Assert.Equal(1, encoder.Process(samples));
        SentPacket packet = Assert.Single(packets);
        return new FneTrafficFrame(
            FneTrafficProtocol.P25,
            peerId: 1,
            sourceId,
            channel.Definition.DestinationId,
            slot: null,
            callType: "GROUP",
            frameType: "VOICE",
            subtype: "LDU1",
            packet.PacketSequence,
            streamId,
            packet.Payload);
    }

    private static IReadOnlyList<FneTrafficFrame> CreateNativeDmrVoice(
        ChannelViewModel channel,
        ReadOnlySpan<short> samples,
        uint sourceId,
        uint streamId)
    {
        var packets = new List<SentPacket>();
        using var backend = new SoftwareVocoderBackend();
        using IVocoderSession vocoder = backend.CreateSession(VocoderMode.DmrAmbe);
        using var encoder = new DmrTxAudioSession(
            sourceId,
            channel.Definition.DestinationId,
            channel.Definition.Slot,
            streamId,
            vocoder,
            (payload, sequence, outboundStreamId) => packets.Add(new SentPacket(
                FneTrafficProtocol.Dmr,
                payload.ToArray(),
                sequence,
                outboundStreamId)));

        Assert.Equal(3, encoder.Process(samples));
        return packets.Select((packet, index) => new FneTrafficFrame(
            FneTrafficProtocol.Dmr,
            peerId: 1,
            sourceId,
            channel.Definition.DestinationId,
            channel.Definition.Slot,
            callType: "GROUP",
            frameType: index == 0 ? "VOICE_SYNC" : "VOICE",
            subtype: "VOICE",
            packet.PacketSequence,
            streamId,
            packet.Payload)).ToArray();
    }

    private static void AssertNativeDmrAudio(IEnumerable<SentPacket> packets)
    {
        using var backend = new SoftwareVocoderBackend();
        using IVocoderSession decoder = backend.CreateSession(VocoderMode.DmrAmbe);
        var decoded = new short[VocoderFrameSizes.PcmSamplesPerFrame];
        long absoluteSampleTotal = 0;
        int sampleCount = 0;
        foreach (SentPacket packet in packets)
        {
            byte[] ambe = DmrVoicePacketCodec.ExtractAmbe(packet.Payload);
            for (int offset = 0; offset < ambe.Length; offset += VocoderFrameSizes.HalfRateCodewordBytes)
            {
                Assert.Equal(0, decoder.Decode(
                    ambe.AsSpan(offset, VocoderFrameSizes.HalfRateCodewordBytes),
                    decoded));
                absoluteSampleTotal += decoded.Sum(sample => Math.Abs((int)sample));
                sampleCount += decoded.Length;
            }
        }

        Assert.True(absoluteSampleTotal / sampleCount > 100);
    }

    private static void AssertNativeP25Audio(SentPacket packet)
    {
        var traffic = new FneTrafficFrame(
            FneTrafficProtocol.P25,
            peerId: 1,
            sourceId: 890,
            destinationId: 747,
            slot: null,
            callType: "GROUP",
            frameType: "VOICE",
            subtype: packet.Payload[22] == P25DfsiFrameCodec.Ldu1Duid ? "LDU1" : "LDU2",
            packet.PacketSequence,
            packet.StreamId,
            packet.Payload);
        byte[] imbe = new byte[P25DfsiFrameCodec.ImbeBytes];
        bool[] available = new bool[P25DfsiFrameCodec.CodewordsPerLdu];
        Assert.True(P25DfsiFrameCodec.TryExtractImbeFrames(traffic, imbe, available));
        Assert.All(available, Assert.True);

        using var backend = new SoftwareVocoderBackend();
        using IVocoderSession decoder = backend.CreateSession(VocoderMode.P25Imbe);
        var decoded = new short[VocoderFrameSizes.PcmSamplesPerFrame];
        long absoluteSampleTotal = 0;
        for (int offset = 0; offset < imbe.Length; offset += P25DfsiFrameCodec.CodewordBytes)
        {
            Assert.Equal(0, decoder.Decode(
                imbe.AsSpan(offset, P25DfsiFrameCodec.CodewordBytes),
                decoded));
            absoluteSampleTotal += decoded.Sum(sample => Math.Abs((int)sample));
        }

        Assert.True(absoluteSampleTotal / (decoded.Length * available.Length) > 100);
    }

    private static async Task WaitForSentCountAsync(FakeEndpoint endpoint, int expectedCount)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (endpoint.Sent.Count < expectedCount)
            await Task.Delay(10, timeout.Token);
    }

    private sealed class FakeEndpoint(
        string name,
        IReadOnlyList<ChannelViewModel> channels,
        uint sourceId) : IFneTrafficEndpoint, IRadioSession
    {
        public SystemId SystemId => SystemId.FromName(name);
        public bool IsConnectionActive => true;
        public event EventHandler<RadioTrafficRecord>? TrafficReceived;
        public event EventHandler<TalkgroupAuthorityRecord>? AuthorityChanged { add { } remove { } }
        public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask QuiesceAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Emit(FneTrafficFrame frame) => TrafficReceived?.Invoke(this,
            new(SystemId, ChannelIds.ToArray(), frame, DateTimeOffset.UtcNow));
        private uint nextStreamId;
        private readonly ConcurrentQueue<SentPacket> sent = new();

        public string Name => name;
        public IReadOnlyList<ChannelViewModel> Channels => channels;
        public IReadOnlyCollection<TransmitChannelDescriptor> ChannelDescriptors
            => channels.Select(channel => channel.ToTransmitDescriptor()).ToArray();
        public IReadOnlyCollection<ChannelId> ChannelIds
            => channels.Select(channel => new ChannelId(channel.SessionId)).ToArray();
        public bool IsConnected => true;
        public uint? SourceId => sourceId;
        public IReadOnlyList<SentPacket> Sent => sent.ToArray();

        public FneTalkgroupAvailability GetTalkgroupAvailability(
            FneTrafficProtocol protocol,
            uint destinationId,
            byte runtimeSlot)
            => FneTalkgroupAvailability.Pending;

        public uint CreateStreamId() => ++nextStreamId;

        public void SendTraffic(
            FneTrafficProtocol protocol,
            ReadOnlyMemory<byte> payload,
            ushort sequence,
            uint streamId)
            => sent.Enqueue(new SentPacket(protocol, payload.ToArray(), sequence, streamId));

        public void ClearSent() => sent.Clear();
    }

    private sealed record SentPacket(
        FneTrafficProtocol Protocol,
        byte[] Payload,
        ushort PacketSequence,
        uint StreamId);

    private sealed class FakeVocoderBackend : IVocoderBackend
    {
        public string Name => "Patch integration fake";
        public bool IsAvailable => true;
        public IVocoderSession CreateSession(VocoderMode mode) => new FakeVocoderSession();
        public void Dispose() { }
    }

    private sealed class FakeVocoderSession : IVocoderSession
    {
        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword)
        {
            codeword.Fill(0x5A);
            return codeword.Length;
        }

        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples)
        {
            samples.Fill(12_000);
            return 0;
        }

        public int FlushEncode(Span<byte> codeword) => 0;
        public void Dispose() { }
    }
}
