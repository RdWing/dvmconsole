using System.Globalization;
using System.Collections.Specialized;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class RecordingCatalogScaleTests
{
    private static readonly DateTimeOffset ScanTime =
        new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(10_000)]
    [InlineData(100_000)]
    public void TraversalPruningAndKeyedReconciliationStayLinearWithoutPhysicalFiles(
        int candidateCount)
    {
        string nonexistentRoot = Path.Combine(
            Path.GetTempPath(),
            "dvmconsole-synthetic-catalog",
            Guid.NewGuid().ToString("N"));
        var source = new SyntheticCatalogScanSource(candidateCount, ScanTime);
        var store = new RecordingCatalogStore(source);

        RecordingCatalogScanResult result = store.Scan(
            nonexistentRoot,
            retentionDays: 7,
            ScanTime,
            CancellationToken.None);

        Assert.False(Directory.Exists(nonexistentRoot));
        Assert.Equal(candidateCount, source.EnumeratedCandidates);
        Assert.Equal(candidateCount, source.MetadataReads);
        Assert.Equal(candidateCount / 4, source.DeleteAttempts);

        Assert.Equal(candidateCount, result.ScannedFiles);
        Assert.Equal(candidateCount / 4, result.PrunedFiles);
        Assert.Equal(candidateCount / 4, result.Recordings.Count);
        Assert.All(
            result.Recordings,
            recording => Assert.Equal(ScanTime.AddMinutes(-1), recording.UtcStartTime));

        RecordingCatalogOperationMetrics operations = result.Operations;
        Assert.Equal(candidateCount, operations.CandidateVisits);
        Assert.Equal(candidateCount, operations.MetadataReads);
        Assert.Equal(candidateCount, operations.RetentionEvaluations);
        Assert.Equal(candidateCount * 3L / 4, operations.KeyLookups);
        Assert.Equal(candidateCount / 2L, operations.KeyWrites);
        Assert.Equal(candidateCount * 17L / 4, operations.TraversalAndReconciliationWork);
    }

    [Theory]
    [InlineData(10_000)]
    [InlineData(100_000)]
    public void FullHistoryCatalogReconciliationUsesKeyedLinearWorkAndOneReset(
        int recordingCount)
    {
        CallRecordingMetadata[] recordings = Enumerable.Range(0, recordingCount)
            .Select(index => new CallRecordingMetadata
            {
                RecordingId = $"recording-{index.ToString(CultureInfo.InvariantCulture)}",
                Direction = "RX",
                Protocol = "DMR",
                UtcStartTime = ScanTime.AddMilliseconds(-index),
                UtcEndTime = ScanTime.AddMilliseconds(-index + 20),
                FileName = $"recording-{index.ToString("D6", CultureInfo.InvariantCulture)}.opus",
                FilePath = $"/synthetic/recording-{index.ToString(CultureInfo.InvariantCulture)}.opus",
                SystemName = "Synthetic",
                ChannelName = "Dispatch",
                TalkgroupId = (uint)(1000 + index),
                SubscriberId = (uint)(2000 + index),
                StreamId = (uint)(index + 1),
                PlaybackValidated = true
            })
            .ToArray();
        var history = new CallHistoryStore();
        int resetNotifications = 0;
        history.Entries.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset)
                resetNotifications++;
        };

        history.ReplaceRecordingCatalog(recordings);

        Assert.Equal(recordingCount, history.Entries.Count);
        Assert.Equal(1, resetNotifications);
        Assert.Equal(recordings[0].RecordingId, history.Entries[0].Recording?.RecordingId);
        Assert.Equal(recordings[^1].RecordingId, history.Entries[^1].Recording?.RecordingId);
        Assert.True(
            history.LastRecordingCatalogReconciliation.TotalWork <= recordingCount * 6L,
            $"Observed {history.LastRecordingCatalogReconciliation.TotalWork} operations for {recordingCount} recordings.");
    }

    private sealed class SyntheticCatalogScanSource(
        int candidateCount,
        DateTimeOffset scanTime) : IRecordingCatalogScanSource
    {
        private const string Prefix = "synthetic-recording-";

        public int EnumeratedCandidates { get; private set; }

        public int MetadataReads { get; private set; }

        public int DeleteAttempts { get; private set; }

        public bool RootExists(string rootPath)
            => true;

        public IEnumerable<string> EnumerateOpusFiles(
            string rootPath,
            Action inaccessiblePathObserved,
            CancellationToken cancellationToken)
        {
            for (int index = 0; index < candidateCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnumeratedCandidates++;
                yield return $"{Prefix}{index.ToString(CultureInfo.InvariantCulture)}.opus";
            }
        }

        public bool TryRead(
            string opusPath,
            string rootPath,
            out CallRecordingMetadata metadata)
        {
            MetadataReads++;
            int index = int.Parse(
                opusPath.AsSpan(Prefix.Length, opusPath.Length - Prefix.Length - ".opus".Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture);
            int group = index / 4;
            int position = index % 4;
            DateTimeOffset startTime = position switch
            {
                1 => scanTime.AddMinutes(-3),
                2 => scanTime.AddMinutes(-1),
                3 => scanTime.AddMinutes(-2),
                _ => scanTime.AddDays(-8).AddMinutes(-1)
            };
            DateTimeOffset endTime = position == 0
                ? scanTime.AddDays(-8)
                : scanTime;
            metadata = new CallRecordingMetadata
            {
                RecordingId = $"recording-{group.ToString(CultureInfo.InvariantCulture)}",
                UtcStartTime = startTime,
                UtcEndTime = endTime,
                FileName = opusPath,
                FilePath = opusPath
            };
            return true;
        }

        public bool IsSafePath(string opusPath, string rootPath)
            => true;

        public bool TryDelete(string path, string rootPath)
        {
            DeleteAttempts++;
            return true;
        }
    }
}
