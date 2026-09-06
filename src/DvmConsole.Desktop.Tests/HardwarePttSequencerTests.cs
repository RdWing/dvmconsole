// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using DvmConsole.Application;
using DvmConsole.Desktop;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class HardwarePttSequencerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleaseDuringStartupStopsAfterTheCueCompletes(bool serial)
    {
        PttActivationSource source = serial ? PttActivationSource.SerialHardware : PttActivationSource.OsGlobalKeyboard;
        using var gate = new SemaphoreSlim(1, 1);
        var port = new Port();
        var sequencer = new HardwarePttSequencer(gate, port, port, new PttActivationArbiter());
        Task start = sequencer.HandleAsync(true, PttTargetScope.AllSelectedResources, source);
        await port.StartEntered.Task;
        port.Pressed = false;
        Task release = sequencer.HandleAsync(false, PttTargetScope.AllSelectedResources, source);
        Assert.Equal(0, port.Stops);
        Assert.False(release.IsCompleted);
        port.StartCompleted.SetResult();
        await Task.WhenAll(start, release).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, port.Stops);
        Assert.False(port.HasActiveTransmission);
    }

    [Fact]
    public async Task TerminalFenceRejectsAnInputQueuedBehindStartup()
    {
        using var gate = new SemaphoreSlim(0, 1);
        var port = new Port();
        var sequencer = new HardwarePttSequencer(gate, port, port, new PttActivationArbiter());
        Task start = sequencer.HandleAsync(true, PttTargetScope.AllSelectedResources, PttActivationSource.SerialHardware);
        port.InputsAllowed = false;
        gate.Release();
        await start;
        Assert.False(port.StartEntered.Task.IsCompleted);
    }

    private sealed class Port : IHardwarePttSequencePort, IHardwarePttInputState
    {
        public TaskCompletionSource StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StartCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool InputsAllowed { get; set; } = true;
        public bool ToggleMode => false;
        public bool HasActiveTransmission { get; private set; }
        public bool Pressed { get; set; } = true;
        public int Stops { get; private set; }
        public IReadOnlyList<ChannelId> GetSelectedTargets(PttTargetScope scope) => [default];
        public void ReportNoTargets(PttTargetScope scope) { }
        public void ObserveActivation(PttActivationSource source) { }
        public async Task StartTargetsAsync(IReadOnlyList<ChannelId> targets)
        {
            StartEntered.SetResult();
            await StartCompleted.Task;
            HasActiveTransmission = true;
        }
        public Task StopActiveAsync()
        {
            if (HasActiveTransmission)
                Stops++;
            HasActiveTransmission = false;
            return Task.CompletedTask;
        }
        public bool IsInputPressed(PttTargetScope scope, PttActivationSource source) => Pressed;
        public void ReleaseKeyboardToggleLatch(PttTargetScope scope, PttActivationSource source) => Pressed = false;
        public void ReleaseAllKeyboardToggleLatches() => Pressed = false;
    }
}
