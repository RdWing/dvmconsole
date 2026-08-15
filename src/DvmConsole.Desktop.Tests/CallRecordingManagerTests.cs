using DvmConsole.Core.Configuration;
using DvmConsole.Desktop;
using DvmConsole.FneClient;
using System.Text.Json;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class CallRecordingManagerTests
{
    [Fact]
    public void StartsAndFinalizesOneWaveFilePerVoiceStream()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-recording-tests", Guid.NewGuid().ToString("N"));
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "analog"
        });
        using var manager = new CallRecordingManager(root);
        channel.SetRecordingEnabled(true);

        try
        {
            Assert.True(channel.TryApplyTraffic("System 1", Traffic("VOICE", "VOICE", 7)));
            short[] firstSamples = new short[800];
            firstSamples[0] = -100;
            firstSamples[1] = 100;
            manager.WriteSamples(channel, firstSamples);
            Assert.Single(manager.ActivePaths);

            manager.ObserveTraffic(channel, Traffic("TERMINATOR", "TERMINATOR", 7));
            Assert.Empty(manager.ActivePaths);

            byte[] first = File.ReadAllBytes(Directory.GetFiles(root, "*.wav", SearchOption.AllDirectories).Single());
            Assert.Equal(1644, first.Length);
            Assert.Equal((byte)'R', first[0]);
            Assert.Equal((uint)1600, BitConverter.ToUInt32(first, 40));

            string metadataPath = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Single();
            CallRecordingMetadata metadata = JsonSerializer.Deserialize<CallRecordingMetadata>(File.ReadAllText(metadataPath))!;
            Assert.Equal("ANALOG", metadata.Protocol);
            Assert.Equal("System 1", metadata.SystemName);
            Assert.Equal("Dispatch", metadata.ChannelName);
            Assert.Equal((uint)42, metadata.SubscriberId);
            Assert.Equal((uint)7, metadata.StreamId);
            Assert.Equal(100L, metadata.DurationMs);
            Assert.Equal(first.Length, metadata.FileSizeBytes);
            Assert.Single(manager.LoadRecordings());

            Assert.True(channel.TryApplyTraffic("System 1", Traffic("VOICE", "VOICE", 8)));
            manager.WriteSamples(channel, new short[] { 1, 2 });
            manager.ObserveTraffic(channel, Traffic("TERMINATOR", "TERMINATOR", 8));

            Assert.Equal(2, Directory.GetFiles(root, "*.wav", SearchOption.AllDirectories).Length);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RetentionRemovesExpiredRecordingAndKeepsRecentRecording()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-recording-tests", Guid.NewGuid().ToString("N"));
        using var manager = new CallRecordingManager(root, retentionDays: 7);
        Directory.CreateDirectory(root);

        try
        {
            WriteCatalogEntry(root, "old", DateTimeOffset.UtcNow.AddDays(-8));
            WriteCatalogEntry(root, "recent", DateTimeOffset.UtcNow.AddDays(-2));

            Assert.Equal(1, manager.PruneExpired());
            Assert.Single(manager.LoadRecordings());
            Assert.Equal("recent.wav", manager.LoadRecordings()[0].FileName);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void IgnoredSourceDoesNotStartRecording()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-recording-tests", Guid.NewGuid().ToString("N"));
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "analog"
        });
        using var manager = new CallRecordingManager(root, shouldRecordSource: (_, sourceId) => sourceId != 42);
        channel.SetRecordingEnabled(true);

        try
        {
            Assert.True(channel.TryApplyTraffic("System 1", Traffic("VOICE", "VOICE", 12)));
            manager.WriteSamples(channel, new short[] { 1, 2, 3 });

            Assert.Empty(manager.ActivePaths);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RecordingPathResolutionRejectsFilesOutsideRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-recording-tests", Guid.NewGuid().ToString("N"));
        using var manager = new CallRecordingManager(root);
        string outside = Path.Combine(Path.GetTempPath(), $"dvmconsole-outside-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(outside, [1, 2, 3]);

        try
        {
            Assert.False(manager.TryGetRecordingPath(new CallRecordingMetadata { FilePath = outside }, out _));
        }
        finally
        {
            if (File.Exists(outside))
                File.Delete(outside);
        }
    }

    [Fact]
    public void DeletesCatalogRecordingAndSidecarWithinRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-recording-tests", Guid.NewGuid().ToString("N"));
        using var manager = new CallRecordingManager(root);
        Directory.CreateDirectory(root);
        string wavPath = Path.Combine(root, "recording.wav");
        string sidecarPath = Path.ChangeExtension(wavPath, ".json");
        File.WriteAllBytes(wavPath, [1, 2, 3]);
        File.WriteAllText(sidecarPath, JsonSerializer.Serialize(new CallRecordingMetadata
        {
            FilePath = wavPath,
            FileName = Path.GetFileName(wavPath),
            UtcStartTime = DateTimeOffset.UtcNow.AddSeconds(-1),
            UtcEndTime = DateTimeOffset.UtcNow,
            SampleRate = 8000,
            BitsPerSample = 16,
            ChannelCount = 1
        }));

        try
        {
            CallRecordingMetadata metadata = manager.LoadRecordings().Single();
            Assert.True(manager.DeleteRecording(metadata));
            Assert.False(File.Exists(wavPath));
            Assert.False(File.Exists(sidecarPath));
            Assert.Empty(manager.LoadRecordings());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteCatalogEntry(string root, string name, DateTimeOffset endTime)
    {
        string directory = Path.Combine(root, "2026-08-14", "System 1");
        Directory.CreateDirectory(directory);
        string wavPath = Path.Combine(directory, $"{name}.wav");
        File.WriteAllBytes(wavPath, [1, 2, 3]);
        var metadata = new CallRecordingMetadata
        {
            UtcStartTime = endTime.AddSeconds(-1),
            UtcEndTime = endTime,
            FilePath = wavPath,
            FileName = Path.GetFileName(wavPath),
            FileSizeBytes = 3,
            SampleRate = 8000,
            BitsPerSample = 16,
            ChannelCount = 1,
            Protocol = "ANALOG",
            SystemName = "System 1",
            ChannelName = "Dispatch"
        };
        File.WriteAllText(Path.ChangeExtension(wavPath, ".json"), JsonSerializer.Serialize(metadata));
    }

    private static FneTrafficFrame Traffic(string frameType, string subtype, uint streamId)
        => new(
            FneTrafficProtocol.Analog,
            peerId: 1,
            sourceId: 42,
            destinationId: 99,
            slot: null,
            callType: "GROUP",
            frameType,
            subtype,
            packetSequence: 1,
            streamId,
            payload: []);
}
