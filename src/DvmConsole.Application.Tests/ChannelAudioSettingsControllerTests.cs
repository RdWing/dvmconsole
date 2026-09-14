// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using Xunit;

namespace DvmConsole.Application.Tests;

public sealed class ChannelAudioSettingsControllerTests
{
    [Theory]
    [InlineData(true, 9, 4)]
    [InlineData(false, -9, -1)]
    [InlineData(true, double.NaN, 1)]
    [InlineData(false, double.PositiveInfinity, 0)]
    public async Task SavesNormalizedIntentBeforeUpdatingStateAndAudio(bool gain, double input, double expected)
    {
        var state = new ChannelOperatorState();
        ChannelReceivePreferenceChange? saved = null;
        double? applied = null;
        var controller = new ChannelAudioSettingsController(_ => state,
            (_, change, _) => { saved = change; return ValueTask.CompletedTask; }, Apply, Apply, () => false);

        await (gain ? controller.SetGainAsync(default, input) : controller.SetBalanceAsync(default, input));

        Assert.Equal(expected, gain ? saved!.Gain : saved!.Balance);
        Assert.Equal(expected, applied);
        Assert.Null(gain ? saved!.Balance : saved!.Gain);

        Task Apply(ChannelId id, double value, CancellationToken token)
        {
            Assert.NotNull(saved);
            Assert.Equal(expected, gain ? state.Snapshot.Gain : state.Snapshot.Balance);
            applied = value;
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetirementDuringSaveCannotApplyLateAudioIntent(bool gain)
    {
        var state = new ChannelOperatorState();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool stopping = false;
        int applied = 0;
        var controller = new ChannelAudioSettingsController(_ => state,
            (_, _, _) => new(pending.Task), Apply, Apply, () => stopping);
        Task command = gain ? controller.SetGainAsync(default, 2) : controller.SetBalanceAsync(default, -1);

        stopping = true;
        pending.SetResult();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => command);
        Assert.Equal(1, state.Snapshot.Gain);
        Assert.Equal(0, state.Snapshot.Balance);
        Assert.Equal(0, applied);

        Task Apply(ChannelId id, double value, CancellationToken token)
        {
            applied++;
            return Task.CompletedTask;
        }
    }
}
