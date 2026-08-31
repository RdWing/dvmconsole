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

        await coordinator.SendAsync(
            channel,
            endpoint,
            new short[VocoderFrameSizes.PcmSamplesPerFrame + 1]);

        Assert.Equal([0, 1, ushort.MaxValue], endpoint.PacketSequences);
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

        await coordinator.SendAsync(channel, endpoint, new short[160]);

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

        await coordinator.SendAsync(channel, endpoint, samples);

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

    private static ChannelViewModel CreateP25Channel()
        => new(new ChannelConfiguration
        {
            Name = "Alert",
            System = "Test",
            Tgid = "100",
            Mode = "p25"
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
            ReadOnlySpan<byte> payload,
            ushort packetSequence,
            uint outboundStreamId)
            => packetSequences.Enqueue(packetSequence);
    }

    private sealed class RecordingVocoderBackend : IVocoderBackend
    {
        public string Name => "Recording P25 vocoder";
        public bool IsAvailable => true;
        public RecordingVocoderSession Session { get; } = new();

        public IVocoderSession CreateSession(VocoderMode mode)
        {
            Assert.Equal(VocoderMode.P25Imbe, mode);
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
