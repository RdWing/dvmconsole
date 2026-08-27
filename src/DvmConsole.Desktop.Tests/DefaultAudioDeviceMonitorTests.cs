using DvmConsole.Audio;
using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class DefaultAudioDeviceMonitorTests
{
    [Fact]
    public async Task ReportsOnlyTheDirectionsWhoseTopologyChanged()
    {
        var provider = new FakeTopologyProvider
        {
            Current = new AudioDeviceTopology("input-1", "output-1")
        };
        var changes = new List<AudioDeviceTopologyChange>();
        await using var monitor = new DefaultAudioDeviceMonitor(
            provider,
            (change, _) =>
            {
                changes.Add(change);
                return Task.CompletedTask;
            });

        await monitor.CheckNowAsync();
        await monitor.CheckNowAsync();
        provider.Current = new AudioDeviceTopology("input-2", "output-1");
        await monitor.CheckNowAsync();
        provider.Current = new AudioDeviceTopology("input-2", "output-2");
        await monitor.CheckNowAsync();

        Assert.Collection(
            changes,
            change =>
            {
                Assert.True(change.InputChanged);
                Assert.False(change.OutputChanged);
            },
            change =>
            {
                Assert.False(change.InputChanged);
                Assert.True(change.OutputChanged);
            });
    }

    [Fact]
    public async Task RetriesAChangeWhenItsHandlerFails()
    {
        var provider = new FakeTopologyProvider
        {
            Current = new AudioDeviceTopology("input-1", "output-1")
        };
        int attempts = 0;
        await using var monitor = new DefaultAudioDeviceMonitor(
            provider,
            (_, _) =>
            {
                attempts++;
                return attempts == 1
                    ? Task.FromException(new IOException("route is still changing"))
                    : Task.CompletedTask;
            });
        await monitor.CheckNowAsync();
        provider.Current = new AudioDeviceTopology("input-2", "output-1");

        await Assert.ThrowsAsync<IOException>(() => monitor.CheckNowAsync());
        await monitor.CheckNowAsync();

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task BacksOffWhileStableAndResetsWhenTopologyChanges()
    {
        var provider = new FakeTopologyProvider
        {
            Current = new AudioDeviceTopology("input-1", "output-1")
        };
        await using var monitor = new DefaultAudioDeviceMonitor(
            provider,
            (_, _) => Task.CompletedTask,
            pollInterval: TimeSpan.FromSeconds(1),
            maximumPollInterval: TimeSpan.FromSeconds(5));

        await monitor.CheckNowAsync();
        Assert.Equal(TimeSpan.FromSeconds(1), monitor.CurrentPollInterval);
        await monitor.CheckNowAsync();
        Assert.Equal(TimeSpan.FromSeconds(2), monitor.CurrentPollInterval);
        await monitor.CheckNowAsync();
        Assert.Equal(TimeSpan.FromSeconds(4), monitor.CurrentPollInterval);
        await monitor.CheckNowAsync();
        Assert.Equal(TimeSpan.FromSeconds(5), monitor.CurrentPollInterval);

        provider.Current = new AudioDeviceTopology("input-2", "output-1");
        await monitor.CheckNowAsync();

        Assert.Equal(TimeSpan.FromSeconds(1), monitor.CurrentPollInterval);
    }

    [Fact]
    public void UsesThePhysicalDefaultIdentityWhenTheDeviceListUsesASyntheticEntry()
    {
        var backend = new SyntheticDefaultAudioBackend
        {
            DefaultOutputIdentity = "built-in-endpoint"
        };
        var provider = new AudioBackendDeviceTopologyProvider(() => backend);
        AudioDeviceTopology first = provider.Read();

        backend.DefaultOutputIdentity = "headset-endpoint";
        AudioDeviceTopology second = provider.Read();

        Assert.Equal(first.InputSignature, second.InputSignature);
        Assert.NotEqual(first.OutputSignature, second.OutputSignature);
    }

    private sealed class FakeTopologyProvider : IAudioDeviceTopologyProvider
    {
        public required AudioDeviceTopology Current { get; set; }

        public AudioDeviceTopology Read() => Current;
    }

    private sealed class SyntheticDefaultAudioBackend :
        IAudioBackend,
        IDefaultAudioDeviceIdentityProvider
    {
        public string DefaultOutputIdentity { get; set; } = "output";
        public string Name => "synthetic-default";

        public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
            => [new AudioDeviceInfo("default", "System default", direction, true)];

        public string? GetDefaultDeviceIdentity(AudioDirection direction)
            => direction == AudioDirection.Input ? "input-endpoint" : DefaultOutputIdentity;

        public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format)
            => throw new NotSupportedException();

        public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
