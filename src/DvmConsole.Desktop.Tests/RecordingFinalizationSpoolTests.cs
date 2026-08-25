using DvmConsole.FneClient;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class RecordingFinalizationSpoolTests
{
    [Fact]
    public void PersistsLoadsAndCompletesOneValidatedJob()
    {
        string root = CreateRoot();
        try
        {
            RecordingFinalizationDescriptor descriptor = CreateDescriptor(root);
            Directory.CreateDirectory(Path.GetDirectoryName(descriptor.WavePath)!);
            File.WriteAllBytes(descriptor.WavePath, [1, 2, 3]);
            var spool = new RecordingFinalizationSpool(root);

            string descriptorPath = spool.Persist(descriptor);
            RecordingFinalizationDescriptor loaded = Assert.Single(spool.LoadPending());

            Assert.Equal(descriptor.JobId, loaded.JobId);
            Assert.Equal(descriptor.WavePath, loaded.WavePath);
            Assert.Equal(descriptor.OutputPath, loaded.OutputPath);
            Assert.Equal(descriptor.StreamIds, loaded.StreamIds);
            Assert.True(File.Exists(descriptorPath));
            Assert.Equal(1, spool.GetHealth().PendingJobs);

            spool.Complete(loaded);

            Assert.False(File.Exists(descriptorPath));
            Assert.False(File.Exists(descriptor.WavePath));
            Assert.Equal(0, spool.GetHealth().PendingJobs);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RejectsAJobWhoseWaveEscapesTheActiveSpool()
    {
        string root = CreateRoot();
        string outsideWave = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.wav");
        try
        {
            File.WriteAllBytes(outsideWave, [1]);
            RecordingFinalizationDescriptor descriptor = CreateDescriptor(root) with
            {
                WavePath = outsideWave
            };
            var spool = new RecordingFinalizationSpool(root);

            Assert.Throws<InvalidDataException>(() => spool.Persist(descriptor));
        }
        finally
        {
            if (File.Exists(outsideWave))
                File.Delete(outsideWave);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CorruptDescriptorIsQuarantinedWithoutScanningOutsideTheRoot()
    {
        string root = CreateRoot();
        try
        {
            string active = Path.Combine(root, ".active");
            Directory.CreateDirectory(active);
            File.WriteAllText(Path.Combine(active, "bad.finalize.json"), "{ definitely not json");
            var spool = new RecordingFinalizationSpool(root);

            Assert.Empty(spool.LoadPending());
            RecordingFinalizationSpoolHealth health = spool.GetHealth();

            Assert.Equal(1, health.QuarantinedJobs);
            Assert.Single(Directory.EnumerateFiles(
                Path.Combine(active, "quarantine"),
                "*.bad"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OneHundredJobsSurviveCrashRestartWithoutLosingSourceRecordings()
    {
        string root = CreateRoot();
        try
        {
            var firstProcess = new RecordingFinalizationSpool(root);
            var expected = new Dictionary<Guid, byte[]>();
            for (int index = 0; index < 100; index++)
            {
                Guid jobId = Guid.Parse($"00000000-0000-0000-0000-{index + 1:000000000000}");
                byte[] source = [(byte)index, (byte)(index + 1), (byte)(index + 2)];
                RecordingFinalizationDescriptor descriptor = CreateDescriptor(root) with
                {
                    JobId = jobId,
                    WavePath = Path.Combine(root, ".active", $"{jobId:N}.wav"),
                    OutputPath = Path.Combine(root, "2026-08-24", "Test", $"{jobId:N}.opus"),
                    StreamIds = [(uint)(index + 1)]
                };
                Directory.CreateDirectory(Path.GetDirectoryName(descriptor.WavePath)!);
                File.WriteAllBytes(descriptor.WavePath, source);
                firstProcess.Persist(descriptor);
                expected.Add(jobId, source);
            }

            // Simulate a process disappearing after durable persistence and a
            // second process reconstructing all work solely from the spool.
            var restartedProcess = new RecordingFinalizationSpool(root);
            RecordingFinalizationDescriptor[] resumed = restartedProcess.LoadPending().ToArray();

            Assert.Equal(100, resumed.Length);
            Assert.Equal(100, restartedProcess.GetHealth().PendingJobs);
            Assert.All(resumed, descriptor =>
            {
                Assert.True(expected.TryGetValue(descriptor.JobId, out byte[]? source));
                Assert.True(File.Exists(descriptor.WavePath));
                Assert.Equal(source, File.ReadAllBytes(descriptor.WavePath));
            });

            // Loading is idempotent across another restart and never consumes
            // a valid WAV before successful finalization.
            var secondRestart = new RecordingFinalizationSpool(root);
            Assert.Equal(100, secondRestart.LoadPending().Count);
            Assert.All(resumed, descriptor => Assert.True(File.Exists(descriptor.WavePath)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DescriptorlessActiveWaveIsPreservedForManualRecovery()
    {
        string root = CreateRoot();
        try
        {
            string active = Path.Combine(root, ".active");
            Directory.CreateDirectory(active);
            string wavePath = Path.Combine(active, $"{Guid.NewGuid():N}.wav");
            byte[] source = [0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4];
            File.WriteAllBytes(wavePath, source);
            var spool = new RecordingFinalizationSpool(root);

            Assert.Equal(1, spool.RecoverOrphanedWaveFiles());

            string recovered = Assert.Single(Directory.EnumerateFiles(
                Path.Combine(active, "quarantine"),
                "*.wav.orphan"));
            Assert.Equal(source, File.ReadAllBytes(recovered));
            Assert.False(File.Exists(wavePath));
            Assert.Equal(1, spool.GetHealth().QuarantinedJobs);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DiscoveryIsCachedSoHealthChecksDoNotRescanLiveActiveFiles()
    {
        string root = CreateRoot();
        try
        {
            string active = Path.Combine(root, ".active");
            Directory.CreateDirectory(active);
            string startupOrphan = Path.Combine(active, $"{Guid.NewGuid():N}.wav");
            File.WriteAllBytes(startupOrphan, [1]);
            var spool = new RecordingFinalizationSpool(root);

            Assert.Empty(spool.LoadPending());
            Assert.Equal(1, spool.GetHealth().QuarantinedJobs);

            string liveWave = Path.Combine(active, $"{Guid.NewGuid():N}.wav");
            File.WriteAllBytes(liveWave, [2]);
            Assert.Equal(1, spool.RecoverOrphanedWaveFiles());
            Assert.True(File.Exists(liveWave));
            Assert.Equal(1, spool.GetHealth().QuarantinedJobs);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static RecordingFinalizationDescriptor CreateDescriptor(string root)
    {
        Guid jobId = Guid.NewGuid();
        return new RecordingFinalizationDescriptor(
            jobId,
            DateTimeOffset.Parse("2026-08-24T00:00:00Z"),
            root,
            Path.Combine(root, ".active", $"{jobId:N}.wav"),
            Path.Combine(root, "2026-08-24", "Test", "recording.opus"),
            8_000,
            1,
            16,
            FneTrafficProtocol.P25,
            "P25",
            "RX",
            "InboundRadio",
            DateTimeOffset.Parse("2026-08-24T00:00:00Z"),
            DateTimeOffset.Parse("2026-08-24T00:00:01Z"),
            "Test",
            "Dispatch",
            3100,
            1001,
            "Unit 1001",
            51,
            [51],
            false,
            null,
            null,
            7);
    }

    private static string CreateRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dvmconsole-spool-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
