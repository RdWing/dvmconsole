using DvmConsole.Core.Configuration;
using DvmConsole.Audio;
using DvmConsole.Desktop;
using DvmConsole.FneClient;
using DvmConsole.Media;
using DvmConsole.Vocoder;
using fnecore.DMR;
using System.Text.Json;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class CallRecordingManagerTests
{
    [Fact]
    public async Task CompletingAnotherJobDoesNotFinalizeAnActiveTransmitRecording()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-recording-tests", Guid.NewGuid().ToString("N"));
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "analog"
        });
        channel.SetRecordingEnabled(true);
        await using var manager = new CallRecordingManager(
            root,
            faultHandler: null,
            retentionDays: CallRecordingManager.DefaultRetentionDays,
            shouldRecordSource: null,
            finalizationQueueCapacity: 4,
            finalizeRecording: (descriptor, _, _) => Task.FromResult(
                new RecordingFinalizationResult(null, descriptor.StreamId, null, null)));

        try
        {
            manager.WriteTransmitSamples(channel, streamId: 90, sourceId: 7, ActiveSamples());
            string transmitWavePath = Assert.Single(manager.ActivePaths);

            manager.WriteSamples(channel, streamId: 41, sourceId: 8, ActiveSamples());
            Task<RecordingFinalizationResult> receiveFinalized = NextFinalizationAsync(manager);
            manager.StopStream(channel, 41);

            Assert.Equal((uint)41, (await receiveFinalized).StreamId);
            Assert.Equal(0, manager.ScheduledFinalizationCount);
            Assert.True(File.Exists(transmitWavePath));

            Task<RecordingFinalizationResult> transmitFinalized = NextFinalizationAsync(manager);
            manager.StopTransmit(channel);

            Assert.Equal((uint)90, (await transmitFinalized).StreamId);
            Assert.False(File.Exists(transmitWavePath));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingTransmitSourceIsReportedWithoutEscapingPttShutdown()
    {
        if (OperatingSystem.IsWindows())
            return;

        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-recording-tests", Guid.NewGuid().ToString("N"));
        Exception? reported = null;
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "analog"
        });
        channel.SetRecordingEnabled(true);
        using var manager = new CallRecordingManager(
            root,
            faultHandler: (_, exception) => reported = exception);

        try
        {
            manager.WriteTransmitSamples(channel, streamId: 90, sourceId: 7, ActiveSamples());
            File.Delete(Assert.Single(manager.ActivePaths));

            manager.StopTransmit(channel);

            Assert.IsType<InvalidDataException>(reported);
            Assert.Empty(manager.ActivePaths);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FinalizationOverflowRemainsDurableWithoutCreatingUnboundedInMemoryWork()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-recording-tests", Guid.NewGuid().ToString("N"));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "analog"
        });
        channel.SetRecordingEnabled(true);
        await using var manager = new CallRecordingManager(
            root,
            faultHandler: null,
            retentionDays: CallRecordingManager.DefaultRetentionDays,
            shouldRecordSource: null,
            finalizationQueueCapacity: 1,
            finalizeRecording: async (descriptor, _, cancellationToken) =>
            {
                if (Interlocked.Increment(ref started) == 1)
                    firstStarted.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return new RecordingFinalizationResult(null, descriptor.StreamId, null, null);
            });

        try
        {
            for (uint streamId = 1; streamId <= 3; streamId++)
            {
                manager.WriteSamples(channel, streamId, sourceId: 7, ActiveSamples());
                manager.StopStream(channel, streamId);
            }

            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(3, manager.FinalizationHealth.PendingJobs);
            Assert.InRange(manager.ScheduledFinalizationCount, 1, 2);

            release.TrySetResult();
            await WaitForAsync(() => manager.FinalizationHealth.PendingJobs == 0);

            Assert.Equal(3, Volatile.Read(ref started));
        }
        finally
        {
            release.TrySetResult();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ActiveRecordingPersistsCrashDescriptorBeforeCallClose()
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
            manager.WriteSamples(channel, streamId: 42, sourceId: 7, ActiveSamples());

            string wavePath = Assert.Single(manager.ActivePaths);
            string descriptorPath = Path.ChangeExtension(wavePath, ".finalize.json");
            Assert.True(File.Exists(wavePath));
            Assert.True(File.Exists(descriptorPath));
            RecordingFinalizationDescriptor descriptor = JsonSerializer.Deserialize<RecordingFinalizationDescriptor>(
                File.ReadAllText(descriptorPath))!;
            Assert.Equal(wavePath, descriptor.WavePath);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.OutputPath));

            Task<RecordingFinalizationResult> finalized = NextFinalizationAsync(manager);
            manager.StopStream(channel, 42);

            Assert.True((await finalized).IsPlayable);
            Assert.False(File.Exists(descriptorPath));
            Assert.False(File.Exists(wavePath));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartupRepairsAndFinalizesInterruptedActiveWave()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-recording-tests", Guid.NewGuid().ToString("N"));
        Guid jobId = Guid.NewGuid();
        string activeDirectory = Path.Combine(root, ".active");
        string wavePath = Path.Combine(activeDirectory, $"{jobId:N}.wav");
        string outputPath = Path.Combine(root, "2026-08-24", "System 1", "recovered.opus");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        try
        {
            using (var writer = new PcmWavFileWriter(wavePath, PcmAudioFormat.Voice8KhzMono16Bit))
                writer.Write(Enumerable.Repeat((short)900, 800).ToArray());
            using (var stream = new FileStream(wavePath, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                stream.Position = 4;
                stream.Write(new byte[4]);
                stream.Position = 40;
                stream.Write(new byte[4]);
                stream.Flush(flushToDisk: true);
            }

            var descriptor = new RecordingFinalizationDescriptor(
                jobId,
                DateTimeOffset.Parse("2026-08-24T00:00:00Z"),
                root,
                wavePath,
                outputPath,
                8_000,
                1,
                16,
                FneTrafficProtocol.Analog,
                "ANALOG",
                "RX",
                "InboundRadio",
                DateTimeOffset.Parse("2026-08-24T00:00:00Z"),
                DateTimeOffset.Parse("2026-08-24T00:00:00Z"),
                "System 1",
                "Dispatch",
                99,
                7,
                "Unit 7",
                42,
                [42],
                false,
                null,
                null,
                7);
            new RecordingFinalizationSpool(root).PersistCaptureSnapshot(descriptor);

            await using var restarted = new CallRecordingManager(root);
            await WaitForAsync(() => File.Exists(outputPath));

            CallRecordingMetadata metadata = Assert.Single(restarted.LoadRecordings());
            Assert.True(metadata.IsPlayable);
            Assert.Equal((uint)42, metadata.StreamId);
            Assert.False(File.Exists(wavePath));
            Assert.Empty(Directory.GetFiles(activeDirectory, "*.finalize.json"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EpisodeFragmentsProduceOneTarWithEveryPhysicalStreamId()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-recording-tests", Guid.NewGuid().ToString("N"));
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "p25"
        });
        using var manager = new CallRecordingManager(root);
        channel.SetRecordingEnabled(true);

        try
        {
            manager.WriteEpisodeSamples(channel, 41, 41, 7, ActiveSamples());
            manager.WriteEpisodeSamples(channel, 41, 42, 7, ActiveSamples());
            Task<RecordingFinalizationResult> finalized = NextFinalizationAsync(manager);
            manager.StopStream(channel, 41);

            Assert.True((await finalized).IsPlayable);
            CallRecordingMetadata metadata = Assert.Single(manager.LoadRecordings());
            Assert.Equal((uint)41, metadata.StreamId);
            Assert.Equal(new uint[] { 41, 42 }, metadata.StreamIds);
            Assert.Equal(2, metadata.StreamFragmentCount);
            Assert.Equal(CallRecordingMetadata.CurrentSchemaVersion, metadata.SchemaVersion);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EpisodeFragmentMetadataBoundsAttackerControlledStreamIdentities()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-recording-tests", Guid.NewGuid().ToString("N"));
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "p25"
        });
        using var manager = new CallRecordingManager(root);
        channel.SetRecordingEnabled(true);
        const uint primaryStreamId = 41;
        int totalStreams = ReceiveCallEpisodeTracker.MaximumStreamsPerEpisode + 2;

        try
        {
            for (uint offset = 0; offset < totalStreams; offset++)
            {
                manager.WriteEpisodeSamples(
                    channel,
                    primaryStreamId,
                    primaryStreamId + offset,
                    sourceId: 7,
                    ActiveSamples());
            }

            Task<RecordingFinalizationResult> finalized = NextFinalizationAsync(manager);
            manager.StopStream(channel, primaryStreamId);

            Assert.True((await finalized).IsPlayable);
            CallRecordingMetadata metadata = Assert.Single(manager.LoadRecordings());
            Assert.Equal(ReceiveCallEpisodeTracker.MaximumStreamsPerEpisode, metadata.StreamIds.Count);
            Assert.Contains(primaryStreamId, metadata.StreamIds);
            Assert.DoesNotContain(primaryStreamId + 1, metadata.StreamIds);
            Assert.DoesNotContain(primaryStreamId + 2, metadata.StreamIds);
            Assert.Contains(primaryStreamId + checked((uint)totalStreams) - 1, metadata.StreamIds);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DisposalClosesActiveWaveAndDrainsOpusFinalization()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-recording-tests", Guid.NewGuid().ToString("N"));
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "analog"
        });
        var manager = new CallRecordingManager(root);
        channel.SetRecordingEnabled(true);

        try
        {
            manager.WriteSamples(channel, streamId: 42, sourceId: 7, ActiveSamples());
            Task<RecordingFinalizationResult> finalized = NextFinalizationAsync(manager);

            await manager.DisposeAsync();

            RecordingFinalizationResult result = await finalized;
            Assert.True(result.IsPlayable);
            Assert.Empty(manager.ActivePaths);
            Assert.Empty(Directory.GetFiles(root, "*.wav", SearchOption.AllDirectories));
            Assert.Single(Directory.GetFiles(root, "*.opus", SearchOption.AllDirectories));
        }
        finally
        {
            await manager.DisposeAsync();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SilentOnlyRecordingFinalizesWithoutAPlayAction()
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
            Assert.True(channel.TryApplyTraffic("System 1", Traffic("VOICE", "VOICE", 40)));
            manager.WriteSamples(channel, streamId: 40, sourceId: 7, new short[160]);
            Task<RecordingFinalizationResult> finalized = NextFinalizationAsync(manager);
            manager.ObserveTraffic(channel, Traffic("TERMINATOR", "TERMINATOR", 40));

            RecordingFinalizationResult result = await finalized;
            Assert.False(result.IsPlayable);
            Assert.Null(result.Metadata);
            Assert.Contains("no playable voice activity", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(manager.LoadRecordings());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReceiveSamplesUseSuppliedIdentityInsteadOfMutableChannelState()
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
            Assert.True(channel.TryApplyTraffic("System 1", Traffic("VOICE", "VOICE", 42)));
            manager.WriteSamples(channel, streamId: 41, sourceId: 7, ActiveSamples());
            Task<RecordingFinalizationResult> finalized = NextFinalizationAsync(manager);
            manager.StopChannel(channel);
            Assert.True((await finalized).IsPlayable);

            CallRecordingMetadata metadata = Assert.Single(manager.LoadRecordings());
            Assert.Equal((uint)41, metadata.StreamId);
            Assert.Equal((uint)7, metadata.SubscriberId);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentReceiveStreamsKeepIndependentRecordingWriters()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-recording-tests", Guid.NewGuid().ToString("N"));
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "p25"
        });
        using var manager = new CallRecordingManager(root);
        channel.SetRecordingEnabled(true);

        try
        {
            manager.WriteSamples(channel, streamId: 41, sourceId: 7, ActiveSamples());
            manager.WriteSamples(channel, streamId: 42, sourceId: 8, ActiveSamples());

            Assert.Equal(2, manager.ActivePaths.Count);

            Task<RecordingFinalizationResult> firstFinalized = NextFinalizationAsync(manager);
            Assert.True(manager.ObserveTraffic(channel, P25Traffic("TERMINATOR", "TDU", 41)));
            Assert.True((await firstFinalized).IsPlayable);
            Assert.Single(manager.ActivePaths);

            Task<RecordingFinalizationResult> secondFinalized = NextFinalizationAsync(manager);
            Assert.True(manager.ObserveTraffic(channel, P25Traffic("TERMINATOR", "TDU", 42)));
            Assert.True((await secondFinalized).IsPlayable);

            Assert.Empty(manager.ActivePaths);
            Assert.Equal(
                new uint?[] { 41, 42 },
                manager.LoadRecordings().Select(item => item.StreamId).Order().ToArray());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartsAndFinalizesOneOpusFilePerVoiceStream()
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
            firstSamples[0] = -900;
            firstSamples[1] = 900;
            manager.WriteSamples(channel, firstSamples);
            Assert.Single(manager.ActivePaths);

            Task<RecordingFinalizationResult> firstFinalized = NextFinalizationAsync(manager);
            Assert.True(manager.ObserveTraffic(channel, Traffic("TERMINATOR", "TERMINATOR", 7)));
            Assert.Empty(manager.ActivePaths);
            Assert.True((await firstFinalized).IsPlayable);

            Assert.False(manager.ObserveTraffic(channel, Traffic("VOICE", "VOICE", 7)));

            string firstPath = Directory.GetFiles(root, "*.opus", SearchOption.AllDirectories).Single();
            byte[] first = File.ReadAllBytes(firstPath);
            Assert.Equal("OggS", System.Text.Encoding.ASCII.GetString(first, 0, 4));
            await using (var reader = await DvmConsole.Audio.PcmStreamDecoder.OpenAsync(File.OpenRead(firstPath)))
            {
                short[] decoded = new short[1600];
                Assert.True(await reader.ReadSamplesAsync(decoded) > 0);
            }

            Assert.Empty(Directory.GetFiles(root, "*.json", SearchOption.AllDirectories));
            OggOpusTagSet tags = OggOpusTags.Read(firstPath);
            Assert.True(tags.Fields.ContainsKey(OpusRecordingMetadataStore.MetadataTag));
            CallRecordingMetadata metadata = Assert.Single(manager.LoadRecordings());
            Assert.Equal("ANALOG", metadata.Protocol);
            Assert.Equal("System 1", metadata.SystemName);
            Assert.Equal("Dispatch", metadata.ChannelName);
            Assert.Equal((uint)42, metadata.SubscriberId);
            Assert.Equal((uint)7, metadata.StreamId);
            Assert.Equal(100L, metadata.DurationMs);
            Assert.True(metadata.PlaybackValidated);
            Assert.True(metadata.IsPlayable);
            Assert.Equal(first.Length, metadata.FileSizeBytes);
            Assert.EndsWith("_System 1_99_42_CLEAR_7.opus", metadata.FileName, StringComparison.Ordinal);
            Assert.Single(manager.LoadRecordings());

            Assert.True(channel.TryApplyTraffic("System 1", Traffic("VOICE", "VOICE", 8)));
            manager.WriteSamples(channel, streamId: 8, sourceId: 42, ActiveSamples());
            Task<RecordingFinalizationResult> secondFinalized = NextFinalizationAsync(manager);
            manager.ObserveTraffic(channel, Traffic("TERMINATOR", "TERMINATOR", 8));
            Assert.True((await secondFinalized).IsPlayable);

            Assert.Equal(2, Directory.GetFiles(root, "*.opus", SearchOption.AllDirectories).Length);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OpusRecordingIsSmallerThanTheSourcePcm()
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
            Assert.True(channel.TryApplyTraffic("System 1", Traffic("VOICE", "VOICE", 9)));
            short[] samples = Enumerable.Range(0, 8000)
                .Select(index => (short)(Math.Sin(index * Math.PI / 20) * 6000))
                .ToArray();
            manager.WriteSamples(channel, samples);
            Task<RecordingFinalizationResult> finalized = NextFinalizationAsync(manager);
            manager.ObserveTraffic(channel, Traffic("TERMINATOR", "TERMINATOR", 9));
            Assert.True((await finalized).IsPlayable);

            CallRecordingMetadata metadata = manager.LoadRecordings().Single();
            Assert.True(metadata.FileSizeBytes < samples.Length * sizeof(short));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RetentionRemovesExpiredRecordingAndKeepsRecentRecording()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-recording-tests", Guid.NewGuid().ToString("N"));
        using var manager = new CallRecordingManager(root, retentionDays: 7);
        Directory.CreateDirectory(root);

        try
        {
            await WriteCatalogEntryAsync(root, "old", DateTimeOffset.UtcNow.AddDays(-8));
            await WriteCatalogEntryAsync(root, "recent", DateTimeOffset.UtcNow.AddDays(-2));

            Assert.Equal(1, manager.PruneExpired());
            Assert.Single(manager.LoadRecordings());
            Assert.Equal("recent.opus", manager.LoadRecordings()[0].FileName);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RetentionDoesNotFollowDirectoryLinksOutsideTheRecordingRoot()
    {
        string testRoot = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-recording-tests",
            Guid.NewGuid().ToString("N"));
        string recordingRoot = Path.Combine(testRoot, "recordings");
        string outsideRoot = Path.Combine(testRoot, "outside");
        Directory.CreateDirectory(recordingRoot);
        string outsideRecording = await WriteCatalogEntryAsync(
            outsideRoot,
            "outside-old",
            DateTimeOffset.UtcNow.AddDays(-8));
        Directory.CreateSymbolicLink(
            Path.Combine(recordingRoot, "linked-outside"),
            outsideRoot);
        using var manager = new CallRecordingManager(recordingRoot, retentionDays: 7);

        try
        {
            Assert.Equal(0, manager.PruneExpired());
            Assert.Empty(manager.LoadRecordings());
            Assert.True(File.Exists(outsideRecording));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void RetentionRevalidatesAChildDirectoryBeforeDeletingAnEnumeratedFile()
    {
        string testRoot = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-recording-tests",
            Guid.NewGuid().ToString("N"));
        string recordingRoot = Path.Combine(testRoot, "recordings");
        string child = Path.Combine(recordingRoot, "day");
        string movedChild = Path.Combine(recordingRoot, "day-original");
        string outsideRoot = Path.Combine(testRoot, "outside");
        Directory.CreateDirectory(child);
        Directory.CreateDirectory(outsideRoot);
        string candidate = Path.Combine(child, "old.opus");
        File.WriteAllBytes(candidate, [1]);
        string outsideRecording = Path.Combine(outsideRoot, "old.opus");
        File.WriteAllBytes(outsideRecording, [2]);
        var source = new FileRecordingCatalogScanSource(new OpusRecordingMetadataStore());

        try
        {
            string enumerated = Assert.Single(source.EnumerateOpusFiles(
                recordingRoot,
                static () => { },
                CancellationToken.None));
            Directory.Move(child, movedChild);
            Directory.CreateSymbolicLink(child, outsideRoot);

            Assert.Equal(candidate, enumerated);
            Assert.False(source.IsSafePath(enumerated, recordingRoot));
            Assert.False(source.TryDelete(enumerated, recordingRoot));
            Assert.True(File.Exists(outsideRecording));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PersistsTrimAndActivityAnalysisForCompletedRecording()
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
            Assert.True(channel.TryApplyTraffic("System 1", Traffic("VOICE", "VOICE", 22)));
            short[] samples = new short[8000];
            samples[2000] = 1000;
            samples[5000] = -1000;
            manager.WriteSamples(channel, samples);
            Task<RecordingFinalizationResult> finalized = NextFinalizationAsync(manager);
            manager.ObserveTraffic(channel, Traffic("TERMINATOR", "TERMINATOR", 22));
            Assert.True((await finalized).IsPlayable);

            CallRecordingMetadata metadata = manager.LoadRecordings().Single();
            Assert.Equal(8000, metadata.OriginalSampleCount);
            Assert.Equal(2, metadata.ActiveSampleCount);
            Assert.Equal(1000, metadata.PeakAmplitude);
            Assert.Equal(120, metadata.TrimLeadMs);
            Assert.Equal(240, metadata.TrimTailMs);
            Assert.Equal(640L, metadata.DurationMs);
            Assert.Contains("activity 0.0%", metadata.AudioAnalysisText);
            Assert.Contains("trim -120/+240 ms", metadata.AudioAnalysisText);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConcealedP25SlotsRemainInTheRecordingUntilTheTerminator()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-recording-tests", Guid.NewGuid().ToString("N"));
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "p25"
        });
        using var manager = new CallRecordingManager(root);
        channel.SetRecordingEnabled(true);

        try
        {
            const uint streamId = 77;
            manager.WriteSamples(channel, streamId, sourceId: 42, ActiveSamples());
            Assert.False(manager.ObserveTraffic(channel, P25Traffic("VOICE", "LDU1", streamId)));
            manager.WriteSamples(
                channel,
                streamId,
                sourceId: 42,
                new short[P25DfsiFrameCodec.CodewordsPerLdu * VocoderFrameSizes.PcmSamplesPerFrame]);
            manager.WriteSamples(channel, streamId, sourceId: 42, ActiveSamples());

            Task<RecordingFinalizationResult> finalized = NextFinalizationAsync(manager);
            Assert.True(manager.ObserveTraffic(channel, P25Traffic("TERMINATOR", "TDU", streamId)));
            Assert.True((await finalized).IsPlayable);

            CallRecordingMetadata metadata = Assert.Single(manager.LoadRecordings());
            Assert.Equal(11 * VocoderFrameSizes.PcmSamplesPerFrame, metadata.OriginalSampleCount);
            Assert.Equal(220, metadata.DurationMs);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartsAndFinalizesConsoleTransmitRecordingWithDirectionMetadata()
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
            manager.WriteTransmitSamples(channel, streamId: 44, sourceId: 7, ActiveSamples());
            Assert.Single(manager.ActivePaths);

            Task<RecordingFinalizationResult> finalized = NextFinalizationAsync(manager);
            manager.StopChannel(channel);
            Assert.True((await finalized).IsPlayable);

            CallRecordingMetadata metadata = manager.LoadRecordings().Single();
            Assert.Equal("TX", metadata.Direction);
            Assert.Equal("ConsoleTx", metadata.RecordingSourceType);
            Assert.Equal((uint)7, metadata.SubscriberId);
            Assert.Equal((uint)44, metadata.StreamId);
            Assert.EndsWith("_System 1_99_7_CLEAR_44.opus", metadata.FileName, StringComparison.Ordinal);
            Assert.Contains("TX · ConsoleTx", metadata.DetailText, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SelectableTransmitRecordingNamesReflectTheEffectiveSecurityState()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-recording-tests", Guid.NewGuid().ToString("N"));
        var keyRing = new DvmConsole.Media.P25KeyRing("System 1", new KeyContainer
        {
            Keys =
            [
                new KeyEntry
                {
                    KeyId = 0x50,
                    AlgId = fnecore.P25.P25Defines.P25_ALGO_AES,
                    Key = "00112233445566778899AABBCCDDEEFF"
                }
            ]
        });
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "p25",
            Algo = "aes",
            KeyId = "0x50",
            SelectableEncryption = true
        }, keyRing);
        using var manager = new CallRecordingManager(root);
        channel.SetRecordingEnabled(true);

        try
        {
            manager.WriteTransmitSamples(channel, streamId: 51, sourceId: 7, ActiveSamples());
            Task<RecordingFinalizationResult> secureFinalized = NextFinalizationAsync(manager);
            manager.StopTransmit(channel);
            Assert.True((await secureFinalized).IsPlayable);

            channel.RestoreTransmitEncryption(false);
            manager.WriteTransmitSamples(channel, streamId: 52, sourceId: 7, ActiveSamples());
            Task<RecordingFinalizationResult> clearFinalized = NextFinalizationAsync(manager);
            manager.StopTransmit(channel);
            Assert.True((await clearFinalized).IsPlayable);

            CallRecordingMetadata secure = manager.LoadRecordings().Single(recording => recording.StreamId == 51);
            CallRecordingMetadata clear = manager.LoadRecordings().Single(recording => recording.StreamId == 52);
            Assert.True(secure.IsEncrypted);
            Assert.Equal("AES", secure.EncryptionAlgorithm);
            Assert.EndsWith("_System 1_99_7_SECURE_AES_51.opus", secure.FileName, StringComparison.Ordinal);
            Assert.False(clear.IsEncrypted);
            Assert.Equal(string.Empty, clear.EncryptionAlgorithm);
            Assert.EndsWith("_System 1_99_7_CLEAR_52.opus", clear.FileName, StringComparison.Ordinal);
        }
        finally
        {
            keyRing.Dispose();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReceiveRecordingUsesProtocolSecurityMetadataInItsName()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-recording-tests", Guid.NewGuid().ToString("N"));
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "dmr",
            Slot = 1
        });
        using var manager = new CallRecordingManager(root);
        channel.SetRecordingEnabled(true);

        try
        {
            byte[] dmrFrame = new byte[DmrVoicePacketCodec.FrameBytes];
            var privacy = new PrivacyLC
            {
                AlgId = DmrPrivacyAlgorithms.DesOfb,
                KId = 3,
                FID = DmrPrivacyAlgorithms.FeatureId,
                Group = true,
                DstId = 99
            };
            FullLC.EncodePI(privacy, ref dmrFrame);
            new SlotType { ColorCode = 0, DataType = (byte)DMRDataType.VOICE_PI_HEADER }.GetData(ref dmrFrame);
            byte[] packet = new byte[DmrVoicePacketCodec.PacketBytes];
            dmrFrame.CopyTo(packet, DmrVoicePacketCodec.HeaderBytes);
            manager.ObserveTraffic(channel, new FneTrafficFrame(
                FneTrafficProtocol.Dmr,
                peerId: 1,
                sourceId: 42,
                destinationId: 99,
                slot: 0,
                callType: "GROUP",
                frameType: "DATA_SYNC",
                subtype: "VOICE_PI_HEADER",
                packetSequence: 1,
                streamId: 61,
                payload: packet));

            Assert.True(channel.TryApplyTraffic("System 1", new FneTrafficFrame(
                FneTrafficProtocol.Dmr,
                1,
                42,
                99,
                0,
                "GROUP",
                "VOICE",
                "VOICE",
                2,
                61,
                new byte[DmrVoicePacketCodec.PacketBytes])));
            manager.WriteSamples(channel, ActiveSamples());
            Task<RecordingFinalizationResult> finalized = NextFinalizationAsync(manager);
            manager.ObserveTraffic(channel, new FneTrafficFrame(
                FneTrafficProtocol.Dmr,
                1,
                42,
                99,
                0,
                "GROUP",
                "TERMINATOR",
                "TERMINATOR_WITH_LC",
                3,
                61,
                []));
            Assert.True((await finalized).IsPlayable);

            CallRecordingMetadata metadata = manager.LoadRecordings().Single();
            Assert.True(metadata.IsEncrypted);
            Assert.Equal(CallRecordingEncryptionState.Secure, metadata.EffectiveEncryptionState);
            Assert.Equal(DmrPrivacyAlgorithms.DesOfb, metadata.EncryptionAlgorithmId);
            Assert.Equal("DES-OFB", metadata.EncryptionAlgorithm);
            Assert.Equal((ushort)3, metadata.EncryptionKeyIdValue);
            Assert.Equal("0x3", metadata.EncryptionKeyId);
            Assert.EndsWith("_System 1_99_42_SECURE_DES_61.opus", metadata.FileName, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ClearDmrReceiveOnEncryptedChannelUsesObservedCallState()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-recording-tests", Guid.NewGuid().ToString("N"));
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "dmr",
            Slot = 1,
            Algo = "aes",
            KeyId = "3"
        });
        using var manager = new CallRecordingManager(root);
        channel.SetRecordingEnabled(true);

        try
        {
            const uint streamId = 62;
            manager.ObserveTraffic(channel, DmrTraffic("DATA_SYNC", "VOICE_LC_HEADER", streamId));
            manager.WriteSamples(channel, streamId, sourceId: 42, ActiveSamples());
            manager.ObserveTraffic(channel, DmrTraffic("VOICE", "VOICE", streamId));

            Task<RecordingFinalizationResult> finalized = NextFinalizationAsync(manager);
            manager.StopStream(channel, streamId);
            Assert.True((await finalized).IsPlayable);

            CallRecordingMetadata metadata = Assert.Single(manager.LoadRecordings());
            Assert.False(metadata.IsEncrypted);
            Assert.Equal(CallRecordingEncryptionState.Clear, metadata.EffectiveEncryptionState);
            Assert.Null(metadata.EncryptionAlgorithmId);
            Assert.Null(metadata.EncryptionKeyIdValue);
            Assert.EndsWith("_System 1_99_42_CLEAR_62.opus", metadata.FileName, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LateDmrPrivacyHeaderCorrectsProvisionalClearMetadata()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-recording-tests",
            Guid.NewGuid().ToString("N"));
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "dmr",
            Slot = 1
        });
        using var manager = new CallRecordingManager(root);
        channel.SetRecordingEnabled(true);

        try
        {
            const uint streamId = 65;
            manager.ObserveTraffic(channel, DmrTraffic("DATA_SYNC", "VOICE_LC_HEADER", streamId));
            manager.WriteSamples(channel, streamId, sourceId: 42, ActiveSamples());
            manager.ObserveTraffic(channel, DmrTraffic("VOICE", "VOICE", streamId));
            manager.ObserveTraffic(
                channel,
                DmrPrivacyTraffic(streamId, DmrPrivacyAlgorithms.Arc4, keyId: 3));

            Task<RecordingFinalizationResult> finalized = NextFinalizationAsync(manager);
            manager.StopStream(channel, streamId);
            Assert.True((await finalized).IsPlayable);

            CallRecordingMetadata metadata = Assert.Single(manager.LoadRecordings());
            Assert.Equal(CallRecordingEncryptionState.Secure, metadata.EffectiveEncryptionState);
            Assert.Equal(DmrPrivacyAlgorithms.Arc4, metadata.EncryptionAlgorithmId);
            Assert.Equal((ushort)3, metadata.EncryptionKeyIdValue);
            Assert.Contains("_SECURE_RC4_65.opus", metadata.FileName, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LateP25MetadataDeterminesFinalNameAndPreservesWireIdentifiers()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-recording-tests", Guid.NewGuid().ToString("N"));
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "p25"
        });
        using var manager = new CallRecordingManager(root);
        channel.SetRecordingEnabled(true);

        try
        {
            const uint streamId = 63;
            manager.WriteSamples(channel, streamId, sourceId: 42, ActiveSamples());
            manager.ObserveTraffic(channel, P25EncryptedTraffic(streamId, P25EncryptionAlgorithms.Aes, 0x50));

            Task<RecordingFinalizationResult> finalized = NextFinalizationAsync(manager);
            manager.StopStream(channel, streamId);
            Assert.True((await finalized).IsPlayable);

            CallRecordingMetadata metadata = Assert.Single(manager.LoadRecordings());
            Assert.True(metadata.IsEncrypted);
            Assert.Equal(CallRecordingEncryptionState.Secure, metadata.EffectiveEncryptionState);
            Assert.Equal(P25EncryptionAlgorithms.Aes, metadata.EncryptionAlgorithmId);
            Assert.Equal((ushort)0x50, metadata.EncryptionKeyIdValue);
            Assert.EndsWith("_System 1_99_42_SECURE_AES_63.opus", metadata.FileName, StringComparison.Ordinal);

            string encoded = OggOpusTags.Read(metadata.FilePath).Fields[OpusRecordingMetadataStore.MetadataTag];
            using JsonDocument embedded = DecodeMetadata(encoded);
            JsonElement payload = embedded.RootElement;
            Assert.False(payload.TryGetProperty(nameof(CallRecordingMetadata.FilePath), out _));
            Assert.False(payload.TryGetProperty(nameof(CallRecordingMetadata.FileName), out _));
            Assert.False(payload.TryGetProperty(nameof(CallRecordingMetadata.FileSizeBytes), out _));
            Assert.Equal(P25EncryptionAlgorithms.Aes, payload.GetProperty(nameof(CallRecordingMetadata.EncryptionAlgorithmId)).GetByte());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ClearP25ReceiveOnFixedSecureChannelFinalizesAsClear()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-recording-tests", Guid.NewGuid().ToString("N"));
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "p25",
            Algo = "aes",
            KeyId = "0x50"
        });
        using var manager = new CallRecordingManager(root);
        channel.SetRecordingEnabled(true);

        try
        {
            const uint streamId = 66;
            manager.WriteSamples(channel, streamId, sourceId: 42, ActiveSamples());
            manager.ObserveTraffic(channel, P25ClearTraffic(streamId));

            Task<RecordingFinalizationResult> finalized = NextFinalizationAsync(manager);
            manager.StopStream(channel, streamId);
            Assert.True((await finalized).IsPlayable);

            CallRecordingMetadata metadata = Assert.Single(manager.LoadRecordings());
            Assert.False(metadata.IsEncrypted);
            Assert.Equal(CallRecordingEncryptionState.Clear, metadata.EffectiveEncryptionState);
            Assert.Null(metadata.EncryptionAlgorithmId);
            Assert.Null(metadata.EncryptionKeyIdValue);
            Assert.EndsWith("_System 1_99_42_CLEAR_66.opus", metadata.FileName, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LateEntryDmrReceiveWithoutCallMetadataRemainsUnknown()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-recording-tests", Guid.NewGuid().ToString("N"));
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "dmr",
            Slot = 1,
            Algo = "aes",
            KeyId = "3"
        });
        using var manager = new CallRecordingManager(root);
        channel.SetRecordingEnabled(true);

        try
        {
            const uint streamId = 64;
            manager.ObserveTraffic(channel, DmrTraffic("VOICE", "VOICE", streamId));
            manager.WriteSamples(channel, streamId, sourceId: 42, ActiveSamples());

            Task<RecordingFinalizationResult> finalized = NextFinalizationAsync(manager);
            manager.StopStream(channel, streamId);
            Assert.True((await finalized).IsPlayable);

            CallRecordingMetadata metadata = Assert.Single(manager.LoadRecordings());
            Assert.False(metadata.IsEncrypted);
            Assert.Equal(CallRecordingEncryptionState.Unknown, metadata.EffectiveEncryptionState);
            Assert.Equal("Unknown", metadata.EncryptionText);
            Assert.EndsWith("_System 1_99_42_UNKNOWN_64.opus", metadata.FileName, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task NxdnReceivePreservesExactCipherAndKeyIdentifiers()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-recording-tests", Guid.NewGuid().ToString("N"));
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Dispatch",
            System = "System 1",
            Tgid = "99",
            Mode = "nxdn"
        });
        using var manager = new CallRecordingManager(root);
        channel.SetRecordingEnabled(true);

        try
        {
            const uint streamId = 65;
            byte[] payload = NxdnVoicePacketCodec.CreateCallControlPacket(
                42,
                99,
                group: true,
                NxdnVoicePacketCodec.VoiceCallMessageType,
                frameSequence: 1,
                cipherType: NxdnPrivacyAlgorithms.Ehr,
                keyId: 7);
            manager.ObserveTraffic(channel, new FneTrafficFrame(
                FneTrafficProtocol.Nxdn,
                1,
                42,
                99,
                null,
                "GROUP",
                "VOICE",
                "VCALL",
                1,
                streamId,
                payload));
            manager.WriteSamples(channel, streamId, sourceId: 42, ActiveSamples());

            Task<RecordingFinalizationResult> finalized = NextFinalizationAsync(manager);
            manager.StopStream(channel, streamId);
            Assert.True((await finalized).IsPlayable);

            CallRecordingMetadata metadata = Assert.Single(manager.LoadRecordings());
            Assert.Equal(CallRecordingEncryptionState.Secure, metadata.EffectiveEncryptionState);
            Assert.Equal(NxdnPrivacyAlgorithms.Ehr, metadata.EncryptionAlgorithmId);
            Assert.Equal((ushort)7, metadata.EncryptionKeyIdValue);
            Assert.Equal("EHR", metadata.EncryptionAlgorithm);
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
        string outside = Path.Combine(Path.GetTempPath(), $"dvmconsole-outside-{Guid.NewGuid():N}.opus");
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
    public async Task ChangesRecordingRootOnlyWhenNoStreamIsActive()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-recording-tests", Guid.NewGuid().ToString("N"));
        string nextRoot = Path.Combine(Path.GetTempPath(), "dvmconsole-recording-tests", Guid.NewGuid().ToString("N"));
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
            Assert.True(manager.TrySetRootPath(nextRoot, out string error));
            Assert.Equal(string.Empty, error);
            Assert.Equal(Path.GetFullPath(nextRoot), manager.RootPath);

            Assert.True(channel.TryApplyTraffic("System 1", Traffic("VOICE", "VOICE", 31)));
            manager.WriteSamples(channel, ActiveSamples());
            Assert.False(manager.TrySetRootPath(root, out error));
            Assert.Contains("active recordings", error, StringComparison.OrdinalIgnoreCase);

            Task<RecordingFinalizationResult> finalized = NextFinalizationAsync(manager);
            manager.StopChannel(channel);
            Assert.True((await finalized).IsPlayable);
            Assert.True(manager.TrySetRootPath(root, out error));
            Assert.Equal(string.Empty, error);
            Assert.Equal(Path.GetFullPath(root), manager.RootPath);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            if (Directory.Exists(nextRoot))
                Directory.Delete(nextRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DeletesCatalogRecordingWithoutDeletingSameStemJson()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-recording-tests", Guid.NewGuid().ToString("N"));
        using var manager = new CallRecordingManager(root);
        string opusPath = await WriteCatalogEntryAsync(root, "recording", DateTimeOffset.UtcNow);
        string jsonPath = Path.ChangeExtension(opusPath, ".json");
        File.WriteAllText(jsonPath, "{\"keep\":true}");

        try
        {
            CallRecordingMetadata metadata = manager.LoadRecordings().Single();
            Assert.True(manager.DeleteRecording(metadata));
            Assert.False(File.Exists(opusPath));
            Assert.True(File.Exists(jsonPath));
            Assert.Empty(manager.LoadRecordings());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void IgnoresJsonMetadataDuringCatalogLoadingAndRetentionPruning()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-recording-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string unrelatedPath = Path.Combine(root, "operator-notes.txt");
        File.WriteAllText(unrelatedPath, "retain this file");
        File.WriteAllText(Path.Combine(root, "malicious.json"), JsonSerializer.Serialize(new CallRecordingMetadata
        {
            FilePath = unrelatedPath,
            UtcEndTime = DateTimeOffset.UnixEpoch
        }));
        using var manager = new CallRecordingManager(root, retentionDays: 7);

        try
        {
            Assert.Empty(manager.LoadRecordings());
            Assert.Equal(0, manager.PruneExpired(DateTimeOffset.UtcNow));
            Assert.True(File.Exists(unrelatedPath));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void IgnoresLegacyWavSidecar()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-recording-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string wavPath = Path.Combine(root, "legacy.wav");
        using (var writer = new PcmWavFileWriter(wavPath, PcmAudioFormat.Voice8KhzMono16Bit))
            writer.Write(Enumerable.Repeat((short)1200, 800).ToArray());
        string sidecarPath = Path.ChangeExtension(wavPath, ".json");
        File.WriteAllText(sidecarPath, JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            Direction = "RX",
            Protocol = "ANALOG",
            UtcStartTime = DateTimeOffset.UnixEpoch,
            UtcEndTime = DateTimeOffset.UnixEpoch.AddMilliseconds(100),
            DurationMs = 100,
            FilePath = wavPath,
            FileName = "legacy.wav",
            SystemName = "System 1",
            ChannelName = "Dispatch",
            TalkgroupId = 99,
            SubscriberId = 42,
            StreamId = 7
        }));
        using var manager = new CallRecordingManager(root);

        try
        {
            Assert.Empty(manager.LoadRecordings());
            Assert.True(File.Exists(wavPath));
            Assert.True(File.Exists(sidecarPath));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task IgnoresOpusSidecarWithoutEmbeddedMetadata()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-recording-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string wavPath = Path.Combine(root, "source.wav");
        string opusPath = Path.Combine(root, "legacy.opus");
        using (var writer = new PcmWavFileWriter(wavPath, PcmAudioFormat.Voice8KhzMono16Bit))
            writer.Write(Enumerable.Repeat((short)1200, 800).ToArray());
        await OpusRecordingEncoder.EncodeWaveFileAsync(wavPath, opusPath);
        File.Delete(wavPath);

        string sidecarPath = Path.ChangeExtension(opusPath, ".json");
        var legacyMetadata = new CallRecordingMetadata
        {
            SchemaVersion = 2,
            RecordingId = "legacy-opus-recording",
            Direction = "RX",
            Protocol = "ANALOG",
            UtcStartTime = DateTimeOffset.UnixEpoch,
            UtcEndTime = DateTimeOffset.UnixEpoch.AddMilliseconds(100),
            DurationMs = 100,
            FilePath = opusPath,
            FileName = Path.GetFileName(opusPath),
            FileSizeBytes = new FileInfo(opusPath).Length,
            SampleRate = 8000,
            BitsPerSample = 16,
            ChannelCount = 1,
            OriginalSampleCount = 800,
            ActiveSampleCount = 800,
            PeakAmplitude = 1200,
            SystemName = "System 1",
            ChannelName = "Dispatch",
            TalkgroupId = 99,
            SubscriberId = 42,
            StreamId = 7,
            PlaybackValidated = true
        };
        File.WriteAllText(sidecarPath, JsonSerializer.Serialize(legacyMetadata));
        using var manager = new CallRecordingManager(root);

        try
        {
            Assert.Empty(manager.LoadRecordings());
            Assert.True(File.Exists(sidecarPath));
            Assert.False(OggOpusTags.Read(opusPath).Fields.ContainsKey(OpusRecordingMetadataStore.MetadataTag));

            await using IAudioPcmStreamReader reader = await PcmStreamDecoder.OpenAsync(File.OpenRead(opusPath));
            short[] decoded = new short[1600];
            Assert.True(await reader.ReadSamplesAsync(decoded) > 0);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void IgnoresDamagedOpusAndItsJsonFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-recording-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string opusPath = Path.Combine(root, "damaged.opus");
        string sidecarPath = Path.ChangeExtension(opusPath, ".json");
        File.WriteAllBytes(opusPath, [1, 2, 3]);
        File.WriteAllText(sidecarPath, JsonSerializer.Serialize(new CallRecordingMetadata
        {
            SchemaVersion = 2,
            RecordingId = "damaged-opus-recording",
            FilePath = opusPath,
            FileName = Path.GetFileName(opusPath),
            UtcStartTime = DateTimeOffset.UnixEpoch,
            UtcEndTime = DateTimeOffset.UnixEpoch.AddSeconds(1)
        }));
        using var manager = new CallRecordingManager(root);

        try
        {
            Assert.Empty(manager.LoadRecordings());
            Assert.True(File.Exists(sidecarPath));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(opusPath));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DoesNotRetryLegacyJsonValidation()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-recording-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string wavPath = Path.Combine(root, "retry.wav");
        File.WriteAllBytes(wavPath, [1, 2, 3]);
        string sidecarPath = Path.ChangeExtension(wavPath, ".json");
        File.WriteAllText(sidecarPath, JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            Direction = "RX",
            Protocol = "ANALOG",
            UtcStartTime = DateTimeOffset.UnixEpoch,
            UtcEndTime = DateTimeOffset.UnixEpoch.AddMilliseconds(100),
            DurationMs = 100,
            FilePath = wavPath,
            SystemName = "System 1",
            ChannelName = "Dispatch",
            TalkgroupId = 99,
            StreamId = 7
        }));
        using var manager = new CallRecordingManager(root);

        try
        {
            Assert.Empty(manager.LoadRecordings());

            File.Delete(wavPath);
            using (var writer = new PcmWavFileWriter(wavPath, PcmAudioFormat.Voice8KhzMono16Bit))
                writer.Write(Enumerable.Repeat((short)1200, 800).ToArray());

            Assert.Empty(manager.LoadRecordings());
            Assert.True(File.Exists(sidecarPath));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void IgnoresCopiedSidecarsWithTheSameRecordingIdentity()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvmconsole-recording-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string wavPath = Path.Combine(root, "dispatch.wav");
        File.WriteAllBytes(wavPath, [1, 2, 3]);
        var metadata = new CallRecordingMetadata
        {
            SchemaVersion = 2,
            RecordingId = "same-recording",
            PlaybackValidated = true,
            UtcStartTime = DateTimeOffset.UnixEpoch,
            FilePath = wavPath,
            FileName = "dispatch.wav"
        };
        string json = JsonSerializer.Serialize(metadata);
        File.WriteAllText(Path.Combine(root, "dispatch.json"), json);
        File.WriteAllText(Path.Combine(root, "dispatch-copy.json"), json);
        using var manager = new CallRecordingManager(root);

        try
        {
            Assert.Empty(manager.LoadRecordings());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> WriteCatalogEntryAsync(
        string root,
        string name,
        DateTimeOffset endTime)
    {
        string directory = Path.Combine(root, "2026-08-14", "System 1");
        Directory.CreateDirectory(directory);
        string wavPath = Path.Combine(directory, $"{name}.wav");
        string opusPath = Path.Combine(directory, $"{name}.opus");
        using (var writer = new PcmWavFileWriter(wavPath, PcmAudioFormat.Voice8KhzMono16Bit))
            writer.Write(Enumerable.Repeat((short)1200, 800).ToArray());
        var metadata = new CallRecordingMetadata
        {
            SchemaVersion = 2,
            UtcStartTime = endTime.AddSeconds(-1),
            UtcEndTime = endTime,
            DurationMs = 100,
            FilePath = opusPath,
            FileName = Path.GetFileName(opusPath),
            SampleRate = 8000,
            BitsPerSample = 16,
            ChannelCount = 1,
            OriginalSampleCount = 800,
            ActiveSampleCount = 800,
            PeakAmplitude = 1200,
            Protocol = "ANALOG",
            SystemName = "System 1",
            ChannelName = "Dispatch",
            PlaybackValidated = true
        };
        await OpusRecordingEncoder.EncodeWaveFileAsync(
            wavPath,
            opusPath,
            new OpusRecordingMetadataStore().CreateTags(metadata));
        File.Delete(wavPath);
        return opusPath;
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

    private static FneTrafficFrame P25Traffic(string frameType, string subtype, uint streamId)
        => new(
            FneTrafficProtocol.P25,
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

    private static FneTrafficFrame P25EncryptedTraffic(
        uint streamId,
        byte algorithmId,
        ushort keyId)
        => new(
            FneTrafficProtocol.P25,
            peerId: 1,
            sourceId: 42,
            destinationId: 99,
            slot: null,
            callType: "GROUP",
            frameType: "VOICE",
            subtype: "LDU1",
            packetSequence: 1,
            streamId,
            P25DfsiFrameCodec.CreateEncryptedLdu1Payload(
                42,
                99,
                new byte[P25DfsiFrameCodec.ImbeBytes],
                new P25DfsiFrameCodec.P25EncryptionMetadata(
                    algorithmId,
                    keyId,
                    [1, 2, 3, 4, 5, 6, 7, 8, 9])));

    private static FneTrafficFrame P25ClearTraffic(uint streamId)
        => new(
            FneTrafficProtocol.P25,
            peerId: 1,
            sourceId: 42,
            destinationId: 99,
            slot: null,
            callType: "GROUP",
            frameType: "VOICE",
            subtype: "LDU1",
            packetSequence: 1,
            streamId,
            P25DfsiFrameCodec.CreateLdu1Payload(
                42,
                99,
                new byte[P25DfsiFrameCodec.ImbeBytes]));

    private static FneTrafficFrame DmrTraffic(
        string frameType,
        string subtype,
        uint streamId)
        => new(
            FneTrafficProtocol.Dmr,
            peerId: 1,
            sourceId: 42,
            destinationId: 99,
            slot: 0,
            callType: "GROUP",
            frameType,
            subtype,
            packetSequence: 1,
            streamId,
            new byte[DmrVoicePacketCodec.PacketBytes]);

    private static FneTrafficFrame DmrPrivacyTraffic(
        uint streamId,
        byte algorithmId,
        byte keyId)
    {
        byte[] frame = new byte[DmrVoicePacketCodec.FrameBytes];
        var privacy = new PrivacyLC
        {
            AlgId = algorithmId,
            KId = keyId,
            FID = DmrPrivacyAlgorithms.FeatureId,
            Group = true,
            DstId = 99
        };
        FullLC.EncodePI(privacy, ref frame);
        new SlotType
        {
            ColorCode = 0,
            DataType = (byte)DMRDataType.VOICE_PI_HEADER
        }.GetData(ref frame);
        byte[] packet = new byte[DmrVoicePacketCodec.PacketBytes];
        frame.CopyTo(packet, DmrVoicePacketCodec.HeaderBytes);
        return new FneTrafficFrame(
            FneTrafficProtocol.Dmr,
            peerId: 1,
            sourceId: 42,
            destinationId: 99,
            slot: 0,
            callType: "GROUP",
            frameType: "DATA_SYNC",
            subtype: "VOICE_PI_HEADER",
            packetSequence: 1,
            streamId,
            packet);
    }

    private static JsonDocument DecodeMetadata(string encoded)
    {
        string base64 = encoded.Replace('-', '+').Replace('_', '/');
        base64 = (base64.Length % 4) switch
        {
            0 => base64,
            2 => $"{base64}==",
            3 => $"{base64}=",
            _ => throw new InvalidDataException("Invalid base64url metadata.")
        };
        return JsonDocument.Parse(Convert.FromBase64String(base64));
    }

    private static Task<RecordingFinalizationResult> NextFinalizationAsync(CallRecordingManager manager)
    {
        var completion = new TaskCompletionSource<RecordingFinalizationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<RecordingFinalizationResult>? handler = null;
        handler = (_, result) =>
        {
            manager.RecordingFinalized -= handler;
            completion.TrySetResult(result);
        };
        manager.RecordingFinalized += handler;
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static short[] ActiveSamples()
        => Enumerable.Repeat((short)900, 160).ToArray();

    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("Timed out waiting for resumed TAR finalization.");
            await Task.Delay(20);
        }
    }
}
