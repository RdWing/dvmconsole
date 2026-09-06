// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Concurrent;
using DvmConsole.Audio;
using DvmConsole.Application;
using DvmConsole.Core.Configuration;
using DvmConsole.Desktop;
using DvmConsole.FneClient;
using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class ToneTransmitCoordinatorTests
{
    [Fact]
    public async Task AnalogTonePreservesAPartialFinalFrame()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Alert",
            System = "Test",
            Tgid = "100",
            Mode = "analog"
        });
        var endpoint = new FakeEndpoint(channel);
        await using var coordinator = new ToneTransmitCoordinator();
        var sendingStates = new List<bool>();
        coordinator.SendingChanged += (_, args) => sendingStates.Add(args.IsSending);

        await coordinator.SendAsync(
            channel.ToTransmitDescriptor(),
            endpoint,
            new short[VocoderFrameSizes.PcmSamplesPerFrame + 1]);
        await coordinator.DrainNotificationsAsync();

        Assert.Equal([0, 1, ushort.MaxValue], endpoint.PacketSequences);
        Assert.Equal([true, false], sendingStates);
    }

    [Fact]
    public async Task ThrowingAndReentrantObserversCannotStrandTheToneGate()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Alert",
            System = "Test",
            Tgid = "100",
            Mode = "analog"
        });
        var endpoint = new FakeEndpoint(channel);
        await using var coordinator = new ToneTransmitCoordinator();
        var observed = new List<bool>();
        coordinator.SendingChanged += (_, _) => throw new InvalidOperationException("observer fault");
        coordinator.SendingChanged += (_, args) =>
        {
            observed.Add(args.IsSending);
            _ = coordinator.IsSending;
        };

        await coordinator.SendAsync(channel.ToTransmitDescriptor(), endpoint, new short[160]);
        await coordinator.DrainNotificationsAsync();
        await coordinator.SendAsync(channel.ToTransmitDescriptor(), endpoint, new short[160]);
        await coordinator.DrainNotificationsAsync();

        Assert.Equal([true, false, true, false], observed);
    }

    [Fact]
    public async Task GeneratedAudioUsesTheLiveAuthoritySnapshot()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Alert",
            System = "Test",
            Tgid = "100",
            Mode = "analog"
        });
        channel.ApplyTalkgroupAvailability(FneTalkgroupAvailability.Unavailable);
        var endpoint = new FakeEndpoint(channel)
        {
            TalkgroupAvailability = FneTalkgroupAvailability.Available
        };
        await using var coordinator = new ToneTransmitCoordinator();

        await coordinator.SendAsync(channel.ToTransmitDescriptor(), endpoint, new short[160]);

        Assert.NotEmpty(endpoint.PacketSequences);
    }

    [Fact]
    public async Task ImportedP25AudioAlwaysUsesTheOrdinaryPcmEncoder()
    {
        ChannelViewModel channel = CreateP25Channel();
        var endpoint = new FakeEndpoint(channel);
        var backend = new RecordingVocoderBackend();
        await using var coordinator = new ToneTransmitCoordinator(
            createVocoderBackend: () => backend);
        short[] samples = new PcmToneGenerator().GenerateTone(
            1_000,
            TimeSpan.FromMilliseconds(180));

        await coordinator.SendAsync(channel.ToTransmitDescriptor(), endpoint, samples);

        Assert.Equal(9, backend.Session.EncodeCalls);
        Assert.Equal(0, backend.Session.SingleToneCalls);
    }

    [Fact]
    public async Task ExplicitP25ToneSequenceRetainsLookupEncoding()
    {
        ChannelViewModel channel = CreateP25Channel();
        var endpoint = new FakeEndpoint(channel);
        var backend = new RecordingVocoderBackend();
        await using var coordinator = new ToneTransmitCoordinator(
            createVocoderBackend: () => backend);
        var sequence = new GeneratedToneSequence([
            GeneratedToneStep.Tone(1_000, TimeSpan.FromMilliseconds(20))
        ]);

        await coordinator.SendAsync([
            new TransmitTarget(channel.ToTransmitDescriptor(), endpoint)], sequence);

        Assert.Equal(1, backend.Session.SingleToneCalls);
    }

    [Fact]
    public async Task ExplicitP25SequenceUsesTheProvidedRenderedPcm()
    {
        ChannelViewModel channel = CreateP25Channel();
        var endpoint = new FakeEndpoint(channel);
        var backend = new RecordingVocoderBackend();
        await using var coordinator = new ToneTransmitCoordinator(
            createVocoderBackend: () => backend);
        var sequence = new GeneratedToneSequence([
            GeneratedToneStep.Dtmf('1', TimeSpan.FromMilliseconds(20))
        ]);
        short[] renderedSamples = Enumerable
            .Repeat<short>(1_234, VocoderFrameSizes.PcmSamplesPerFrame)
            .ToArray();

        await coordinator.SendAsync(
            [new TransmitTarget(channel.ToTransmitDescriptor(), endpoint)],
            sequence,
            renderedSamples);

        Assert.Equal(renderedSamples, backend.Session.EncodedFrames[0]);
        Assert.Equal(0, backend.Session.SingleToneCalls);
    }

    [Theory]
    [InlineData("dmr", VocoderMode.DmrAmbe)]
    [InlineData("nxdn", VocoderMode.NxdnAmbe)]
    public async Task DmrAndNxdnGeneratedTonesUseTheNominalTransmitLevel(
        string mode,
        VocoderMode vocoderMode)
    {
        ChannelViewModel channel = CreateDigitalChannel(mode);
        var endpoint = new FakeEndpoint(channel);
        var backend = new RecordingVocoderBackend(vocoderMode);
        await using var coordinator = new ToneTransmitCoordinator(
            createVocoderBackend: () => backend);
        var sequence = new GeneratedToneSequence([
            GeneratedToneStep.Tone(1_000, TimeSpan.FromMilliseconds(20))
        ]);

        await coordinator.SendAsync([
            new TransmitTarget(channel.ToTransmitDescriptor(), endpoint)], sequence);

        short[] frame = Assert.Single(backend.Session.EncodedFrames.Take(1));
        double meanSquare = frame
            .Select(sample => sample / (double)short.MaxValue)
            .Select(sample => sample * sample)
            .Average();
        double rmsDbfs = 20 * Math.Log10(Math.Sqrt(meanSquare));
        Assert.InRange(
            rmsDbfs,
            ToneTransmitCoordinator.DmrNxdnToneTargetDbfs - 0.1,
            ToneTransmitCoordinator.DmrNxdnToneTargetDbfs + 0.1);
    }

    private static ChannelViewModel CreateP25Channel()
        => CreateDigitalChannel("p25");

    private static ChannelViewModel CreateDigitalChannel(string mode)
        => new(new ChannelConfiguration
        {
            Name = "Alert",
            System = "Test",
            Tgid = "100",
            Mode = mode,
            Slot = 1
        });

    private sealed class FakeEndpoint(ChannelViewModel channel) : IFneTrafficEndpoint
    {
        private readonly ConcurrentQueue<ushort> packetSequences = [];
        private uint streamId;

        public string Name => "Test";
        public IReadOnlyList<ChannelViewModel> Channels { get; } = [channel];
        public IReadOnlyCollection<TransmitChannelDescriptor> ChannelDescriptors
            => Channels.Select(candidate => candidate.ToTransmitDescriptor()).ToArray();
        public IReadOnlyCollection<ChannelId> ChannelIds
            => Channels.Select(candidate => new ChannelId(candidate.SessionId)).ToArray();
        public bool IsConnected => true;
        public uint? SourceId => 1001;
        public IReadOnlyList<ushort> PacketSequences => packetSequences.ToArray();
        public FneTalkgroupAvailability TalkgroupAvailability { get; set; } =
            FneTalkgroupAvailability.Pending;

        public FneTalkgroupAvailability GetTalkgroupAvailability(
            FneTrafficProtocol protocol,
            uint destinationId,
            byte runtimeSlot)
            => TalkgroupAvailability;

        public uint CreateStreamId() => ++streamId;

        public void SendTraffic(
            FneTrafficProtocol protocol,
            ReadOnlyMemory<byte> payload,
            ushort packetSequence,
            uint outboundStreamId)
            => packetSequences.Enqueue(packetSequence);
    }

    private sealed class RecordingVocoderBackend(
        VocoderMode expectedMode = VocoderMode.P25Imbe) : IVocoderBackend
    {
        public string Name => "Recording P25 vocoder";
        public bool IsAvailable => true;
        public RecordingVocoderSession Session { get; } = new();

        public IVocoderSession CreateSession(VocoderMode mode)
        {
            Assert.Equal(expectedMode, mode);
            return Session;
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingVocoderSession : IP25GeneratedToneVocoderSession
    {
        public List<short[]> EncodedFrames { get; } = [];
        public int EncodeCalls { get; private set; }
        public int SingleToneCalls { get; private set; }

        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword)
        {
            EncodeCalls++;
            EncodedFrames.Add(samples.ToArray());
            codeword.Fill(0x55);
            return codeword.Length;
        }

        public int EncodeSingleTone(double frequencyHz, Span<byte> codeword)
        {
            SingleToneCalls++;
            codeword.Fill(0xAA);
            return codeword.Length;
        }

        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples) => 0;
        public void Dispose()
        {
        }
    }
}
