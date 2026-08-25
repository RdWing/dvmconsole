using Xunit;

namespace DvmConsole.Audio.Tests;

public sealed class PcmWriteProgressWatchdogTests
{
    [Fact]
    public async Task IntermittentPartialWritesPreserveEverySampleInOrder()
    {
        var target = new PartialTarget([0, 2, 1, 10]);
        short[] samples = [10, 20, 30, 40, 50];

        await PcmWriteProgressWatchdog.WriteAllAsync(
            target,
            samples,
            samples.Length,
            TimeSpan.FromSeconds(1),
            "no progress",
            CancellationToken.None);

        Assert.Equal([10, 20, 30, 40, 50], target.WrittenSamples);
        Assert.Equal(4, target.WriteCalls);
    }

    [Fact]
    public async Task CancellationWinsWhileTheTargetMakesNoProgress()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        var target = new PartialTarget([0]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await PcmWriteProgressWatchdog.WriteAllAsync(
                target,
                new short[160],
                160,
                TimeSpan.FromSeconds(5),
                "no progress",
                cancellation.Token));
    }

    private sealed class PartialTarget(IReadOnlyList<int> writes) : IPcmWriteTarget
    {
        private int index;
        public List<short> WrittenSamples { get; } = [];
        public int WriteCalls { get; private set; }

        public int Write(short[] samples, int count)
        {
            WriteCalls++;
            int requested = writes[Math.Min(index++, writes.Count - 1)];
            int written = Math.Min(requested, count);
            WrittenSamples.AddRange(samples.AsSpan(0, written).ToArray());
            return written;
        }
    }
}
