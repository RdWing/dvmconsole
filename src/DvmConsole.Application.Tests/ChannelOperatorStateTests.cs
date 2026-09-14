// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ChannelOperatorStateTests
{
    [Fact]
    public void DisablingReceiveClearsSuspensionWithoutDisarmingRecording()
    {
        var state = new ChannelOperatorState();
        state.SetRecordingEnabled(true);
        state.SetAudioEnabled(true);
        state.SetAudioSuspended(true);
        ChannelOperatorSnapshot previous = state.Snapshot;

        state.SetAudioEnabled(false);

        Assert.True(previous.AudioEnabled);
        Assert.True(previous.AudioSuspended);
        Assert.False(state.Snapshot.AudioEnabled);
        Assert.False(state.Snapshot.AudioSuspended);
        Assert.True(state.Snapshot.RecordingEnabled);
        Assert.False(state.SetAudioSuspended(true));
    }

    [Fact]
    public void ReenablingSelectedReceiveClearsTemporarySuspension()
    {
        var state = new ChannelOperatorState();
        state.SetAudioEnabled(true);
        state.SetAudioSuspended(true);

        Assert.True(state.SetAudioEnabled(true));
        Assert.False(state.Snapshot.AudioSuspended);
        ChannelOperatorSnapshot unchanged = state.Snapshot;
        Assert.False(state.SetAudioEnabled(true));
        Assert.Same(unchanged, state.Snapshot);
    }

    [Fact]
    public void TransmitTransitionChangesAtomicallyAndRejectsContradictoryIntent()
    {
        var state = new ChannelOperatorState(transmitEncrypted: true);
        state.SetTransmitTransition(starting: true, stopping: false);
        ChannelOperatorSnapshot starting = state.Snapshot;
        state.SetTransmitTransition(starting: false, stopping: true);

        Assert.True(starting.TransmitStarting);
        Assert.False(starting.TransmitStopping);
        Assert.False(state.Snapshot.TransmitStarting);
        Assert.True(state.Snapshot.TransmitStopping);
        Assert.True(state.Snapshot.TransmitEncrypted);
        Assert.Throws<ArgumentException>(() => state.SetTransmitTransition(true, true));
    }

    [Fact]
    public void UnchangedValuesDoNotPublishAndFailingObserversDoNotBlockOthers()
    {
        var state = new ChannelOperatorState();
        int changes = 0;
        state.Changed += (_, _) => throw new InvalidOperationException("observer failed");
        state.Changed += (_, snapshot) =>
        {
            Assert.True(snapshot.AudioEnabled);
            changes++;
        };
        state.SetAudioEnabled(false);
        state.SetAudioEnabled(true);
        state.SetAudioEnabled(true);
        Assert.Equal(1, changes);
    }

    [Theory]
    [InlineData(double.NaN, 1.0, 0.0)]
    [InlineData(double.PositiveInfinity, 1.0, 0.0)]
    [InlineData(-10.0, 0.0, -1.0)]
    [InlineData(10.0, 4.0, 1.0)]
    public void AudioSettingsKeepExistingNormalization(double input, double gain, double balance)
    {
        var state = new ChannelOperatorState();
        state.SetGain(input);
        state.SetBalance(input);
        state.SetOutputRoute("desktop-device-id");
        Assert.Equal(gain, state.Snapshot.Gain);
        Assert.Equal(balance, state.Snapshot.Balance);
        Assert.Equal("desktop-device-id", state.Snapshot.OutputRoute);
    }

    [Fact]
    public async Task ConcurrentIndependentSelectionsDoNotOverwriteEachOther()
    {
        var state = new ChannelOperatorState();
        await Task.WhenAll(
            Task.Run(() => state.SetTransmitSelected(true)),
            Task.Run(() => state.SetPageSelected(true)),
            Task.Run(() => state.SetAlertSelected(true)),
            Task.Run(() => state.SetRecordingEnabled(true)));

        Assert.True(state.Snapshot.TransmitSelected);
        Assert.True(state.Snapshot.PageSelected);
        Assert.True(state.Snapshot.AlertSelected);
        Assert.True(state.Snapshot.RecordingEnabled);
    }
}
