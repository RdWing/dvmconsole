using DvmConsole.Application;
using DvmConsole.Core.Runtime;
using DvmConsole.Operations;
using DvmConsole.Presentation;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class PresentationContractTests
{
    [Fact]
    public async Task ListItemProjectsCollapsedAndExpandedFieldsAndSendsEachCommandOnce()
    {
        ChannelDescriptor descriptor = CreateDescriptor();
        var commands = new RecordingCommands();
        var state = new ChannelControlSnapshot(
            descriptor.Id,
            ChannelRuntimeState.Receiving,
            "Receiving from Engine 12",
            "Engine 12",
            ReceiveEnabled: true,
            ReceiveActive: true,
            Transmitting: false,
            TransmitSelected: true,
            PageSelected: false,
            AlertSelected: true,
            Recording: false,
            RecordingFinalizing: true,
            RecordingFault: null,
            TarArmed: true,
            OutputRoute: "Headset",
            Gain: 1.5,
            Balance: -0.25,
            EffectiveMuteReason: "system Metro output mute",
            Authority: TargetAuthorityState.Unavailable,
            AuthorityReason: "the FNE does not allow TG 3101 on TS2",
            ObservedReceiveEncrypted: true,
            SelectedTransmitEncrypted: true,
            TransmitKeyAvailable: false,
            Patches: [new ChannelPatchMembership(PatchId.New(), "Dispatch patch", true, true, true)],
            PendingOperation: "Changing route",
            Fault: null,
            RecordingPlayback: true,
            TransmitEncryptionConfigured: true,
            TransmitEncryptionSelectable: true);
        var item = new ChannelListItemViewModel(descriptor, commands, state);

        Assert.Equal("Dispatch", item.Name);
        Assert.Equal("TG 3101", item.TalkgroupText);
        Assert.Equal("DMR TS2", item.ProtocolText);
        Assert.Equal("Receiving from Engine 12", item.StateText);
        Assert.Equal("Last: Engine 12", item.LastCallerText);
        Assert.Equal("RX ON", item.ReceiveButtonText);
        Assert.False(item.IsPttEnabled);
        Assert.Equal("RX secure", item.ReceiveEncryptionText);
        Assert.True(item.IsTransmitEncrypted);
        Assert.True(item.CanToggleEncryption);
        Assert.Contains("TS2", item.AuthorityText, StringComparison.Ordinal);
        Assert.Equal("TAR finalizing", item.TarText);
        Assert.Equal("Recording playback active", item.PlaybackText);
        Assert.Equal("Output route: Headset", item.RouteText);
        Assert.Equal("Volume 1.50×", item.VolumeText);
        Assert.Equal(1d / 6d, item.VolumeSliderValue, precision: 6);
        Assert.Equal("Muted by system Metro output mute", item.MuteText);
        Assert.Equal("Dispatch patch: source →", item.PatchText);
        Assert.Equal("Changing route", item.DiagnosticText);

        item.ToggleExpansion();
        Assert.True(item.IsExpanded);
        await item.ToggleReceiveAsync();
        await item.ToggleTransmitSelectionAsync();
        await item.TogglePageSelectionAsync();
        await item.ToggleAlertSelectionAsync();
        await item.ToggleTransmitEncryptionAsync();
        await item.SetVolumeSliderValueAsync(0);

        Assert.Equal(1, commands.ReceiveCalls);
        Assert.Equal(1, commands.TransmitSelectionCalls);
        Assert.Equal(1, commands.PageSelectionCalls);
        Assert.Equal(1, commands.AlertSelectionCalls);
        Assert.Equal(1, commands.EncryptionCalls);
        Assert.Equal(1, commands.GainCalls);
        Assert.Equal(1, commands.LastGain);
        Assert.Equal(0, commands.BalanceCalls);
    }

    [Fact]
    public async Task RendererNeutralPttReleaseAllStopsEveryActiveChannelExactlyOnce()
    {
        ChannelId first = CreateDescriptor().Id;
        ChannelId second = new(new ChannelSessionId("Metro", ChannelProtocol.P25, 3200, 0, "Tac"));
        var starts = new List<ChannelId>();
        var stops = new List<ChannelId>();
        await using var controller = new ChannelPttController(
            (id, _) =>
            {
                starts.Add(id);
                return ValueTask.FromResult(true);
            },
            (id, _) =>
            {
                stops.Add(id);
                return ValueTask.CompletedTask;
            });
        await controller.PressAsync(first);
        await controller.ToggleAsync(second);

        await controller.ReleaseAllAsync();
        await controller.ReleaseAllAsync();

        Assert.Equal([first, second], starts);
        Assert.Equal(2, stops.Count);
        Assert.Contains(first, stops);
        Assert.Contains(second, stops);
    }

    [Fact]
    public void EquivalentListStateAndMeterSamplesDoNotRaiseRedundantProperties()
    {
        ChannelDescriptor descriptor = CreateDescriptor();
        var commands = new RecordingCommands();
        ChannelControlSnapshot state = CreateState(descriptor.Id);
        var item = new ChannelListItemViewModel(descriptor, commands, state);
        var changed = new List<string?>();
        item.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        item.ApplyState(state with { Patches = state.Patches.ToArray() });
        item.ApplyMeter(new ChannelMeterSample(descriptor.Id, 25, 40, DateTimeOffset.UtcNow));
        int afterFirstMeter = changed.Count;
        item.ApplyMeter(new ChannelMeterSample(descriptor.Id, 25.005, 40.005, DateTimeOffset.UtcNow));

        Assert.Equal(3, afterFirstMeter);
        Assert.Equal(afterFirstMeter, changed.Count);
    }

    [Fact]
    public async Task LifecycleBoundariesReleasePttThroughOneIdempotentPath()
    {
        ChannelId channel = CreateDescriptor().Id;
        int starts = 0;
        int stops = 0;
        var lifecycle = new TestApplicationLifecycle();
        await using var controller = new ChannelPttController(
            (_, _) =>
            {
                starts++;
                return ValueTask.FromResult(true);
            },
            (_, _) =>
            {
                stops++;
                return ValueTask.CompletedTask;
            });
        await using var binding = new ChannelPttLifecycleBinding(
            lifecycle,
            controller.ReleaseAllAsync);

        await controller.PressAsync(channel);
        lifecycle.RaiseDeactivated();
        await binding.WaitForIdleAsync();
        await controller.PressAsync(channel);
        lifecycle.RaiseSuspending();
        await binding.WaitForIdleAsync();
        await controller.PressAsync(channel);
        lifecycle.RaiseStopping();
        await binding.WaitForIdleAsync();

        Assert.Equal(3, starts);
        Assert.Equal(3, stops);
    }

    private static ChannelDescriptor CreateDescriptor()
    {
        var id = new ChannelId(new ChannelSessionId("Metro", ChannelProtocol.Dmr, 3101, 1, "Dispatch"));
        return new ChannelDescriptor(
            id,
            SystemId.FromName("Metro"),
            ZoneId.FromName("Operations"),
            "Dispatch",
            3101,
            "Dmr",
            1,
            false);
    }

    private static ChannelControlSnapshot CreateState(ChannelId id)
        => new(
            id,
            ChannelRuntimeState.Idle,
            "Idle",
            "",
            true,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            null,
            false,
            null,
            1,
            0,
            null,
            TargetAuthorityState.Available,
            null,
            false,
            false,
            true,
            [new ChannelPatchMembership(PatchId.New(), "Patch", true, false, false)],
            null,
            null);

    private sealed class RecordingCommands : IConsoleCommands
    {
        public int ReceiveCalls { get; private set; }
        public int TransmitSelectionCalls { get; private set; }
        public int PageSelectionCalls { get; private set; }
        public int AlertSelectionCalls { get; private set; }
        public int EncryptionCalls { get; private set; }
        public int GainCalls { get; private set; }
        public double LastGain { get; private set; }
        public int BalanceCalls { get; private set; }

        public ValueTask SetReceiveEnabledAsync(ChannelId channelId, bool enabled, CancellationToken cancellationToken = default)
        {
            ReceiveCalls++;
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> BeginPttAsync(ChannelId channelId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(true);

        public ValueTask EndPttAsync(ChannelId channelId, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask SetTransmitSelectedAsync(ChannelId channelId, bool selected, CancellationToken cancellationToken = default)
        {
            TransmitSelectionCalls++;
            return ValueTask.CompletedTask;
        }

        public ValueTask SetPageSelectedAsync(ChannelId channelId, bool selected, CancellationToken cancellationToken = default)
        {
            PageSelectionCalls++;
            return ValueTask.CompletedTask;
        }

        public ValueTask SetAlertSelectedAsync(ChannelId channelId, bool selected, CancellationToken cancellationToken = default)
        {
            AlertSelectionCalls++;
            return ValueTask.CompletedTask;
        }

        public ValueTask SetTransmitEncryptedAsync(ChannelId channelId, bool encrypted, CancellationToken cancellationToken = default)
        {
            EncryptionCalls++;
            return ValueTask.CompletedTask;
        }

        public ValueTask SetChannelGainAsync(ChannelId channelId, double gain, CancellationToken cancellationToken = default)
        {
            GainCalls++;
            LastGain = gain;
            return ValueTask.CompletedTask;
        }

        public ValueTask SetChannelBalanceAsync(ChannelId channelId, double balance, CancellationToken cancellationToken = default)
        {
            BalanceCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestApplicationLifecycle : IApplicationLifecycle
    {
        public bool IsActive { get; private set; } = true;
        public event EventHandler? Activated
        {
            add { }
            remove { }
        }
        public event EventHandler? Deactivated;
        public event EventHandler? Suspending;
        public event EventHandler? Resumed
        {
            add { }
            remove { }
        }
        public event EventHandler? Stopping;

        public void RaiseDeactivated()
        {
            IsActive = false;
            Deactivated?.Invoke(this, EventArgs.Empty);
        }

        public void RaiseSuspending()
            => Suspending?.Invoke(this, EventArgs.Empty);

        public void RaiseStopping()
            => Stopping?.Invoke(this, EventArgs.Empty);
    }
}
