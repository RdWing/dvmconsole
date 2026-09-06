// SPDX-FileCopyrightText: 2025-2026 RdWing
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Concurrent;
using DvmConsole.Audio;
using DvmConsole.Application;
using DvmConsole.Core.Configuration;
using DvmConsole.Desktop;
using DvmConsole.FneClient;
using DvmConsole.Vocoder;
using Xunit;

namespace DvmConsole.Desktop.Tests;

public sealed class TransmitCoordinatorTests
{
    [Fact]
    public async Task StartRejectsAChannelWithActiveReceivePlayback()
    {
        var channel = Channel("A", 100);
        channel.SetAudioEnabled(true);
        channel.MarkReceivePlaybackActive(sourceId: 42, streamId: 7);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend();
        await using var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.StartAsync(channel.ToTransmitDescriptor(), endpoint));

        Assert.Contains("currently receiving", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, audio.OpenCaptureCalls);
        Assert.Empty(coordinator.ActiveChannels);
    }

    [Fact]
    public async Task StartRejectsAnAuthoritativeDmrSlotMismatchBeforeOpeningCapture()
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "DMR Dispatch",
            System = "Test",
            Tgid = "748",
            Mode = "dmr",
            Slot = 2
        });
        var endpoint = new FakeEndpoint("Test", [channel])
        {
            TalkgroupAvailability = FneTalkgroupAvailability.Unavailable
        };
        var audio = new FakeAudioBackend();
        await using var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.StartAsync(channel.ToTransmitDescriptor(), endpoint));

        Assert.Contains("TG 748", exception.Message, StringComparison.Ordinal);
        Assert.Contains("TS2", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, audio.OpenCaptureCalls);
        Assert.Empty(coordinator.ActiveChannels);
    }

    [Fact]
    public async Task StartUsesLiveAuthorityWhenThePresentationSnapshotIsStale()
    {
        ChannelViewModel channel = Channel("Dispatch", 748);
        channel.ApplyTalkgroupAvailability(FneTalkgroupAvailability.Unavailable);
        var endpoint = new FakeEndpoint("Test", [channel])
        {
            TalkgroupAvailability = FneTalkgroupAvailability.Available
        };
        var audio = new FakeAudioBackend();
        await using var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio);
        var activeCounts = new List<int>();
        coordinator.ActiveChannelsChanged += (_, args) => activeCounts.Add(args.ChannelIds.Count);

        await coordinator.StartAsync(channel.ToTransmitDescriptor(), endpoint);

        Assert.Single(coordinator.ActiveChannels);
        Assert.Equal(1, audio.OpenCaptureCalls);
        await coordinator.StopAsync();
        Assert.Equal([1, 0], activeCounts);
    }

    [Fact]
    public async Task AnalogMultiTargetUsesOneCaptureAndCleansUpAllCalls()
    {
        var first = Channel("A", 100);
        var second = Channel("B", 101);
        var endpoint = new FakeEndpoint("Test", [first, second]);
        var audio = new FakeAudioBackend();
        await using var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio);

        await coordinator.StartAsync([
            new TransmitTarget(first.ToTransmitDescriptor(), endpoint),
            new TransmitTarget(second.ToTransmitDescriptor(), endpoint)]);
        await coordinator.ActivateAsync();

        Assert.Equal(1, audio.OpenCaptureCalls);
        Assert.Equal(2, coordinator.ActiveChannels.Count);
        audio.Capture.Emit(new short[160]);
        await WaitForAsync(() => endpoint.Sent.Count == 2);
        Assert.Equal(2, endpoint.Sent.Count); // one voice packet per target

        await coordinator.StopAsync();

        Assert.True(audio.Capture.IsDisposed);
        Assert.True(audio.IsDisposed);
        Assert.Empty(coordinator.ActiveChannels);
        Assert.Equal(4, endpoint.Sent.Count); // matching terminators
    }

    [Fact]
    public async Task SampleObservationUsesAStableSnapshotWhileTransmitStops()
    {
        var first = Channel("A", 100);
        var second = Channel("B", 101);
        var endpoint = new FakeEndpoint("Test", [first, second]);
        var audio = new FakeAudioBackend();
        var firstObservationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowObservationToContinue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new List<ChannelId>();
        await using var coordinator = new ChannelTransmitCoordinator(
            samplesObserver: (channel, _, _, _) =>
            {
                observed.Add(channel);
                if (channel == new ChannelId(first.SessionId))
                {
                    firstObservationEntered.TrySetResult();
                    allowObservationToContinue.Task.GetAwaiter().GetResult();
                }
            },
            createAudioBackend: () => audio);
        await coordinator.StartAsync([
            new TransmitTarget(first.ToTransmitDescriptor(), endpoint),
            new TransmitTarget(second.ToTransmitDescriptor(), endpoint)]);
        await coordinator.ActivateAsync();

        Task publish = Task.Run(() => audio.Capture.Emit(new short[160]));
        await firstObservationEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(1));
        allowObservationToContinue.TrySetResult();
        await publish.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(
            [new ChannelId(first.SessionId), new ChannelId(second.SessionId)],
            observed);
        Assert.Empty(coordinator.ActiveChannels);
    }

    [Fact]
    public async Task SuppressedMicrophoneFramesAreDroppedUntilOperatorAudioMayTransmit()
    {
        var channel = Channel("A", 100);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend();
        var observed = new List<short[]>();
        await using var coordinator = new ChannelTransmitCoordinator(
            samplesObserver: (_, _, _, samples) => observed.Add(samples.ToArray()),
            createAudioBackend: () => audio);

        coordinator.SetMicrophoneAudioSuppressed(true);
        await coordinator.StartAsync(channel.ToTransmitDescriptor(), endpoint);
        await coordinator.ActivateAsync();
        int startupFrameCount = endpoint.Sent.Count;
        audio.Capture.Emit(Enumerable.Repeat((short)1000, 160).ToArray());

        Assert.Equal(startupFrameCount, endpoint.Sent.Count);
        Assert.Empty(observed);

        await coordinator.ReleaseMicrophoneAudioAsync(requireFreshRecoveryCallback: false);
        audio.Capture.Emit(Enumerable.Repeat((short)2000, 160).ToArray());
        await WaitForAsync(() => endpoint.Sent.Count == startupFrameCount + 1);

        Assert.Equal(startupFrameCount + 1, endpoint.Sent.Count);
        Assert.Single(observed);
        Assert.All(observed[0], sample => Assert.Equal((short)2000, sample));
    }

    [Fact]
    public async Task BorrowedSampleObserverFailureDoesNotInterruptTransmitAudio()
    {
        var channel = Channel("A", 100);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend();
        var observationFailures = new ConcurrentQueue<Exception>();
        await using var coordinator = new ChannelTransmitCoordinator(
            createAudioBackend: () => audio,
            borrowedSamplesObserver: (_, _, _, _) => throw new IOException("meter failure"),
            samplesObserverFaultHandler: observationFailures.Enqueue);
        await coordinator.StartAsync(channel.ToTransmitDescriptor(), endpoint);
        await coordinator.ActivateAsync();
        int startupFrameCount = endpoint.Sent.Count;

        audio.Capture.Emit(new short[160]);
        await WaitForAsync(() => endpoint.Sent.Count == startupFrameCount + 1);

        InvalidOperationException failure = Assert.IsType<InvalidOperationException>(
            Assert.Single(observationFailures));
        Assert.Contains("stream 1", failure.Message, StringComparison.Ordinal);
        Assert.Equal(startupFrameCount + 1, endpoint.Sent.Count);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, true)]
    public async Task PreflightIdentifiesColdBluetoothOrUnknownTransitions(
        bool? inputIsBluetooth,
        bool expectedGate)
    {
        var audio = new FakeAudioBackend(inputIsBluetooth: inputIsBluetooth);
        await using var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio);

        MicrophoneStartExpectation expectation =
            await coordinator.InspectNextMicrophoneStartAsync();

        Assert.True(expectation.StartsCold);
        Assert.Equal(inputIsBluetooth, expectation.IsBluetooth);
        Assert.Equal(expectedGate, expectation.RequiresReceiveTransitionGate);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreflightReusesTheCurrentDeviceClassification(
        bool inputIsBluetooth)
    {
        int backendCreations = 0;
        await using var coordinator = new ChannelTransmitCoordinator(
            createAudioBackend: () =>
            {
                backendCreations++;
                return new FakeAudioBackend();
            });

        MicrophoneStartExpectation expectation =
            await coordinator.InspectNextMicrophoneStartAsync(inputIsBluetooth);

        Assert.True(expectation.StartsCold);
        Assert.Equal(inputIsBluetooth, expectation.IsBluetooth);
        Assert.Equal(0, backendCreations);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public async Task ColdBluetoothOrUnknownMicrophoneReadinessUsesFirstSelectedCaptureSample(
        bool? inputIsBluetooth)
    {
        var channel = Channel("A", 100);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend(inputIsBluetooth: inputIsBluetooth);
        await using var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio);

        coordinator.SetMicrophoneAudioSuppressed(true);
        await coordinator.StartAsync(channel.ToTransmitDescriptor(), endpoint);
        Assert.True(coordinator.ActiveMicrophoneStartedCold);
        Assert.Equal(inputIsBluetooth, coordinator.ActiveMicrophoneIsBluetooth);
        Task ready = coordinator.WaitForMicrophoneReadyAsync(TimeSpan.FromSeconds(1));
        Assert.False(ready.IsCompleted);

        audio.Capture.Emit(new short[160]);
        await ready;
        await coordinator.StopAsync();
        Assert.False(coordinator.ActiveMicrophoneStartedCold);
        Assert.Null(coordinator.ActiveMicrophoneIsBluetooth);
    }

    [Fact]
    public async Task KnownNonBluetoothMicrophoneReadinessUsesFirstSelectedCaptureSample()
    {
        var channel = Channel("A", 100);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend(inputIsBluetooth: false);
        await using var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio);

        coordinator.SetMicrophoneAudioSuppressed(true);
        await coordinator.StartAsync(channel.ToTransmitDescriptor(), endpoint);
        Task ready = coordinator.WaitForMicrophoneReadyAsync(TimeSpan.FromSeconds(1));

        audio.Capture.Emit(new short[160]);

        await ready;
        Assert.False(coordinator.ActiveMicrophoneIsBluetooth);
    }

    [Theory]
    [InlineData(false, "stale")]
    [InlineData(true, "faulted")]
    public async Task ActiveTransmitFailsClosedWhenFreshMicrophoneProgressStops(
        bool stopCapture,
        string expectedState)
    {
        var channel = Channel("A", 100);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend(inputIsBluetooth: false);
        await using var coordinator = new ChannelTransmitCoordinator(
            createAudioBackend: () => audio,
            microphoneStaleAfter: TimeSpan.FromMilliseconds(40));
        var faulted = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Faulted += (_, exception) => faulted.TrySetResult(exception);

        coordinator.SetMicrophoneAudioSuppressed(true);
        await coordinator.StartAsync(channel.ToTransmitDescriptor(), endpoint);
        Task<MicrophoneReadinessTiming> ready = coordinator.WaitForMicrophoneReadyAsync(
            TimeSpan.FromSeconds(1));
        audio.Capture.Emit(new short[160]);
        await ready;
        coordinator.SetMicrophoneAudioSuppressed(false);
        int sentBeforeFailure = endpoint.Sent.Count;

        if (stopCapture)
            await audio.Capture.StopAsync();

        Exception failure = await faulted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        audio.Capture.Emit(new short[160]);

        Assert.IsType<IOException>(failure);
        Assert.Contains(expectedState, failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(coordinator.IsMicrophoneAudioSuppressed);
        Assert.Equal(sentBeforeFailure, endpoint.Sent.Count);
    }

    [Fact]
    public async Task StartupGateAllowsColdBluetoothPermitTransitionWithoutFaultingTransmit()
    {
        var channel = Channel("A", 100);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend(inputIsBluetooth: true);
        await using var coordinator = new ChannelTransmitCoordinator(
            createAudioBackend: () => audio,
            microphoneStaleAfter: TimeSpan.FromMilliseconds(40));
        var faulted = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Faulted += (_, exception) => faulted.TrySetResult(exception);

        coordinator.SetMicrophoneAudioSuppressed(true);
        await coordinator.StartAsync(channel.ToTransmitDescriptor(), endpoint);
        Task<MicrophoneReadinessTiming> ready = coordinator.WaitForMicrophoneReadyAsync(
            TimeSpan.FromSeconds(1));
        audio.Capture.Emit(new short[160]);
        await ready;

        // Opening and warming the post-transition Bluetooth output may pause
        // input callbacks longer than the normal active-TX stale threshold.
        await Task.Delay(120);

        Assert.False(faulted.Task.IsCompleted);
        Assert.Single(coordinator.ActiveChannels);

        // Closing the permit-tone output may be the event that allows capture
        // to resume. Keep operator audio gated until the first callback that
        // occurs after the cue has completed.
        Task<TimeSpan> release = coordinator.ReleaseMicrophoneAudioAsync(
            requireFreshRecoveryCallback: true,
            recoveryTimeout: TimeSpan.FromSeconds(10));
        await Task.Delay(120);
        Assert.False(release.IsCompleted);
        Assert.True(coordinator.IsMicrophoneAudioSuppressed);
        Assert.False(faulted.Task.IsCompleted);

        audio.Capture.Emit(new short[160]);
        TimeSpan recovery = await release;
        await Task.Delay(20);

        Assert.True(recovery >= TimeSpan.Zero);
        Assert.False(faulted.Task.IsCompleted);
        Assert.False(coordinator.IsMicrophoneAudioSuppressed);
    }

    [Fact]
    public async Task ColdBluetoothPostCueRecoveryTimesOutWithoutReleasingOperatorAudio()
    {
        var channel = Channel("A", 100);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend(inputIsBluetooth: true);
        await using var coordinator = new ChannelTransmitCoordinator(
            createAudioBackend: () => audio,
            microphoneStaleAfter: TimeSpan.FromMilliseconds(40));
        var faulted = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Faulted += (_, exception) => faulted.TrySetResult(exception);

        coordinator.SetMicrophoneAudioSuppressed(true);
        await coordinator.StartAsync(channel.ToTransmitDescriptor(), endpoint);
        Task<MicrophoneReadinessTiming> ready = coordinator.WaitForMicrophoneReadyAsync(
            TimeSpan.FromSeconds(1));
        audio.Capture.Emit(new short[160]);
        await ready;

        await Assert.ThrowsAsync<TimeoutException>(() =>
            coordinator.ReleaseMicrophoneAudioAsync(
                requireFreshRecoveryCallback: true,
                recoveryTimeout: TimeSpan.FromMilliseconds(50)));

        Assert.True(coordinator.IsMicrophoneAudioSuppressed);
        Assert.False(faulted.Task.IsCompleted);
        Assert.Single(coordinator.ActiveChannels);
    }

    [Fact]
    public async Task PreflightRejectionDoesNotOpenAudio()
    {
        var receiveOnly = new ChannelViewModel(new ChannelConfiguration { Name = "RX", System = "Test", Tgid = "100", Mode = "analog", RxOnly = true });
        var endpoint = new FakeEndpoint("Test", [receiveOnly]);
        var audio = new FakeAudioBackend();
        await using var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync(receiveOnly.ToTransmitDescriptor(), endpoint));

        Assert.Equal(0, audio.OpenCaptureCalls);
        Assert.Empty(endpoint.Sent);
    }

    [Theory]
    [InlineData("dmr", FneTrafficProtocol.Dmr)]
    [InlineData("p25", FneTrafficProtocol.P25)]
    [InlineData("nxdn", FneTrafficProtocol.Nxdn)]
    public async Task DigitalModesCreateTheMatchingProtocolPipeline(string mode, FneTrafficProtocol expectedProtocol)
    {
        var channel = Channel("Digital", 100, mode);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend();
        var vocoder = new FakeVocoderBackend();
        await using var coordinator = new ChannelTransmitCoordinator(
            createAudioBackend: () => audio,
            createVocoderBackend: () => vocoder);

        await Task.Run(() => coordinator.StartAsync(channel.ToTransmitDescriptor(), endpoint));
        await coordinator.ActivateAsync();
        audio.Capture.Emit(new short[160]);

        Assert.True(vocoder.CreateSessionCalls > 0);
        Assert.Contains(endpoint.Sent, sent => sent.Protocol == expectedProtocol);
        await coordinator.StopAsync();
        Assert.True(vocoder.IsDisposed);
    }

    [Theory]
    [InlineData("dmr", false)]
    [InlineData("p25", false)]
    [InlineData("nxdn", false)]
    [InlineData("dmr", true)]
    [InlineData("p25", true)]
    [InlineData("nxdn", true)]
    public async Task LaterTargetFailureDisposesEveryAbandonedResourceOnce(string mode, bool warm)
    {
        ChannelViewModel[] channels = [Channel("First", 100, mode), Channel("Second", 101, mode)];
        var endpoint = new FakeEndpoint("Test", channels);
        var audio = new FakeAudioBackend();
        var vocoder = new FakeVocoderBackend { FailAtSession = 2 };
        var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio, createVocoderBackend: () => vocoder);
        if (warm)
            await coordinator.SetKeepMicrophoneWarmAsync(true);
        await Assert.ThrowsAsync<IOException>(() => coordinator.StartAsync(channels.Select(channel =>
            new TransmitTarget(channel.ToTransmitDescriptor(), endpoint))));
        await coordinator.DisposeAsync();
        Assert.Empty(coordinator.ActiveChannels);
        Assert.Equal(1, audio.DisposeCount);
        Assert.Equal(1, audio.Capture.DisposeCount);
        Assert.Equal(1, vocoder.DisposeCount);
        Assert.Equal(1, Assert.Single(vocoder.Sessions).DisposeCount);
    }

    [Theory]
    [InlineData("dmr", false)]
    [InlineData("p25", false)]
    [InlineData("nxdn", false)]
    [InlineData("dmr", true)]
    [InlineData("p25", true)]
    [InlineData("nxdn", true)]
    public async Task MissingPrivacyKeyRollsBackColdAndWarmPreparation(string mode, bool warm)
    {
        var channel = new ChannelViewModel(new ChannelConfiguration
        {
            Name = "Secure",
            System = "Test",
            Tgid = "100",
            Mode = mode,
            Algo = "aes",
            KeyId = "1"
        });
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend();
        var vocoder = new FakeVocoderBackend();
        var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio, createVocoderBackend: () => vocoder);
        if (warm)
            await coordinator.SetKeepMicrophoneWarmAsync(true);
        // The resolver can lose a key after configuration validation, so exercise preparation itself.
        TransmitChannelDescriptor descriptor = channel.ToTransmitDescriptor() with { CanTransmitByConfiguration = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync(descriptor, endpoint));
        Assert.Empty(coordinator.ActiveChannels);
        Assert.Empty(vocoder.Sessions);
        await coordinator.DisposeAsync();
        Assert.Equal(1, audio.DisposeCount);
        Assert.Equal(1, audio.Capture.DisposeCount);
        Assert.Equal(1, vocoder.DisposeCount);
    }

    [Fact]
    public async Task PreparedDigitalCallEmitsNothingUntilExplicitActivation()
    {
        var channel = Channel("Digital", 100, "dmr");
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend();
        await using var coordinator = new ChannelTransmitCoordinator(
            createAudioBackend: () => audio,
            createVocoderBackend: () => new FakeVocoderBackend());

        await coordinator.StartAsync(channel.ToTransmitDescriptor(), endpoint);
        audio.Capture.Emit(new short[480]);
        Assert.Empty(endpoint.Sent);

        await coordinator.ActivateAsync();
        Assert.Single(endpoint.Sent);
        Assert.True(endpoint.Sent.TryPeek(out var firstPacket));
        Assert.Equal(FneTrafficProtocol.Dmr, firstPacket.Protocol);

        audio.Capture.Emit(new short[480]);
        await WaitForAsync(() => endpoint.Sent.Count == 2);
        Assert.Equal(2, endpoint.Sent.Count);
    }

    [Theory]
    [InlineData("dmr")]
    [InlineData("p25")]
    [InlineData("nxdn")]
    public async Task DigitalModeStartupFailureRollsBackAfterBackgroundStart(string mode)
    {
        var channel = Channel("Digital", 100, mode);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend();
        var vocoder = new FakeVocoderBackend(failCreateSession: true);
        await using var coordinator = new ChannelTransmitCoordinator(
            createAudioBackend: () => audio,
            createVocoderBackend: () => vocoder);

        await Assert.ThrowsAsync<IOException>(() =>
            Task.Run(() => coordinator.StartAsync(channel.ToTransmitDescriptor(), endpoint)));

        Assert.Empty(coordinator.ActiveChannels);
        Assert.True(audio.Capture.IsDisposed);
        Assert.True(audio.IsDisposed);
        Assert.True(vocoder.IsDisposed);
    }

    [Fact]
    public async Task CaptureStartFailureRollsBackCreatedSessionsAndInfrastructure()
    {
        var channel = Channel("Analog", 100);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend(failStart: true);
        await using var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio);

        await Assert.ThrowsAsync<IOException>(() => coordinator.StartAsync(channel.ToTransmitDescriptor(), endpoint));

        Assert.Empty(coordinator.ActiveChannels);
        Assert.True(audio.Capture.IsDisposed);
        Assert.True(audio.IsDisposed);
    }

    [Fact]
    public async Task WarmMicrophoneStaysRunningBetweenCallsUntilDisabled()
    {
        var channel = Channel("Analog", 100);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend();
        await using var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio);

        await coordinator.SetKeepMicrophoneWarmAsync(true);
        Assert.True(audio.Capture.IsRunning);
        Assert.Equal(1, audio.OpenCaptureCalls);

        audio.Capture.Emit(new short[160]);

        await coordinator.StartAsync(channel.ToTransmitDescriptor(), endpoint);
        Assert.False(coordinator.ActiveMicrophoneStartedCold);
        await coordinator.StopAsync();

        Assert.True(audio.Capture.IsRunning);
        Assert.False(audio.Capture.IsDisposed);
        Assert.False(audio.IsDisposed);

        await coordinator.SetKeepMicrophoneWarmAsync(false);

        Assert.True(audio.Capture.IsDisposed);
        Assert.True(audio.IsDisposed);
    }

    [Fact]
    public async Task UnsettledWarmMicrophoneStillUsesColdPermitPolicy()
    {
        var channel = Channel("Analog", 100);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend(inputIsBluetooth: true);
        await using var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio);

        await coordinator.SetKeepMicrophoneWarmAsync(true);
        await coordinator.StartAsync(channel.ToTransmitDescriptor(), endpoint);

        Assert.True(coordinator.ActiveMicrophoneStartedCold);
        Assert.True(coordinator.ActiveMicrophoneIsBluetooth);
    }

    [Fact]
    public async Task StaleWarmMicrophoneIsRestartedBeforePreflight()
    {
        var firstAudio = new FakeAudioBackend(inputDeviceId: "first");
        var replacementAudio = new FakeAudioBackend(inputDeviceId: "replacement");
        IAudioBackend[] backends = [firstAudio, replacementAudio];
        int backendIndex = 0;
        await using var coordinator = new ChannelTransmitCoordinator(
            createAudioBackend: () => backends[backendIndex++],
            microphoneStaleAfter: TimeSpan.FromMilliseconds(20));
        await coordinator.SetKeepMicrophoneWarmAsync(true);
        firstAudio.Capture.Emit(new short[160]);
        await Task.Delay(35);

        MicrophoneStartExpectation expectation =
            await coordinator.InspectNextMicrophoneStartAsync();

        Assert.True(expectation.StartsCold);
        Assert.True(firstAudio.Capture.IsDisposed);
        Assert.True(replacementAudio.Capture.IsRunning);
        Assert.Equal(2, backendIndex);
    }

    [Fact]
    public async Task ReportsPhysicalBluetoothInputForPermitPolicy()
    {
        var channel = Channel("Analog", 100);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend(inputIsBluetooth: true);
        await using var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio);

        await coordinator.StartAsync(channel.ToTransmitDescriptor(), endpoint);

        Assert.True(coordinator.ActiveMicrophoneIsBluetooth);
    }

    [Fact]
    public async Task RefreshesAWarmSystemDefaultMicrophone()
    {
        var firstAudio = new FakeAudioBackend(inputDeviceId: "built-in");
        var headsetAudio = new FakeAudioBackend(inputDeviceId: "headset");
        IAudioBackend[] backends = [firstAudio, headsetAudio];
        int backendIndex = 0;
        await using var coordinator = new ChannelTransmitCoordinator(
            audioInputOptions: new AudioInputProcessingOptions { DeviceId = "default" },
            createAudioBackend: () => backends[backendIndex++]);
        await coordinator.SetKeepMicrophoneWarmAsync(true);

        DefaultInputRefreshResult result = await coordinator.RefreshSystemDefaultInputAsync();

        Assert.Equal(DefaultInputRefreshResult.Refreshed, result);
        Assert.True(firstAudio.Capture.IsDisposed);
        Assert.True(firstAudio.IsDisposed);
        Assert.True(headsetAudio.Capture.IsRunning);
        Assert.Equal("headset", headsetAudio.LastInputDeviceId);
    }

    [Fact]
    public async Task RecreatedWarmMicrophoneUsesUpdatedProcessingOptions()
    {
        var channel = Channel("Analog", 100);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var originalAudio = new FakeAudioBackend();
        var updatedAudio = new FakeAudioBackend();
        IAudioBackend[] backends = [originalAudio, updatedAudio];
        int backendIndex = 0;
        short[]? observedSamples = null;
        await using var coordinator = new ChannelTransmitCoordinator(
            audioInputOptions: new AudioInputProcessingOptions { Gain = 1 },
            samplesObserver: (_, _, _, samples) => observedSamples = samples.ToArray(),
            createAudioBackend: () => backends[backendIndex++]);
        await coordinator.SetKeepMicrophoneWarmAsync(true);

        coordinator.UpdateAudioInputOptions(new AudioInputProcessingOptions { Gain = 2 });
        await coordinator.SetKeepMicrophoneWarmAsync(false);
        await coordinator.SetKeepMicrophoneWarmAsync(true);
        await coordinator.StartAsync(channel.ToTransmitDescriptor(), endpoint);
        await coordinator.ActivateAsync();
        updatedAudio.Capture.Emit(Enumerable.Repeat((short)1_000, 160).ToArray());

        Assert.True(originalAudio.Capture.IsDisposed);
        Assert.NotNull(observedSamples);
        Assert.All(observedSamples, sample => Assert.Equal(2_000, sample));
    }

    [Fact]
    public async Task DefersDefaultMicrophoneRefreshUntilPttEnds()
    {
        var channel = Channel("Analog", 100);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var firstAudio = new FakeAudioBackend(inputDeviceId: "built-in");
        var headsetAudio = new FakeAudioBackend(inputDeviceId: "headset");
        IAudioBackend[] backends = [firstAudio, headsetAudio];
        int backendIndex = 0;
        await using var coordinator = new ChannelTransmitCoordinator(
            audioInputOptions: new AudioInputProcessingOptions { DeviceId = "default" },
            createAudioBackend: () => backends[backendIndex++]);
        await coordinator.SetKeepMicrophoneWarmAsync(true);
        await coordinator.StartAsync(channel.ToTransmitDescriptor(), endpoint);

        DefaultInputRefreshResult result = await coordinator.RefreshSystemDefaultInputAsync();

        Assert.Equal(DefaultInputRefreshResult.DeferredUntilIdle, result);
        Assert.False(firstAudio.IsDisposed);
        await coordinator.StopAsync();
        Assert.True(firstAudio.IsDisposed);
        Assert.True(headsetAudio.Capture.IsRunning);
        Assert.Equal("headset", headsetAudio.LastInputDeviceId);
    }

    [Fact]
    public async Task DoesNotRefreshAFixedMicrophone()
    {
        var audio = new FakeAudioBackend(inputDeviceId: "fixed-input");
        await using var coordinator = new ChannelTransmitCoordinator(
            audioInputOptions: new AudioInputProcessingOptions { DeviceId = "fixed-input" },
            createAudioBackend: () => audio);
        await coordinator.SetKeepMicrophoneWarmAsync(true);

        DefaultInputRefreshResult result = await coordinator.RefreshSystemDefaultInputAsync();

        Assert.Equal(DefaultInputRefreshResult.NotRequired, result);
        Assert.False(audio.IsDisposed);
        Assert.True(audio.Capture.IsRunning);
    }

    [Fact]
    public async Task DisablingWarmMicrophoneDuringTransmitPreservesTheActiveLease()
    {
        var channel = Channel("Analog", 100);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend();
        await using var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio);

        await coordinator.SetKeepMicrophoneWarmAsync(true);
        await coordinator.StartAsync(channel.ToTransmitDescriptor(), endpoint);
        await coordinator.ActivateAsync();
        int before = endpoint.Sent.Count;

        await coordinator.SetKeepMicrophoneWarmAsync(false);
        audio.Capture.Emit(Enumerable.Repeat((short)1000, 160).ToArray());
        await WaitForAsync(() => endpoint.Sent.Count == before + 1);

        Assert.True(audio.Capture.IsRunning);
        Assert.False(audio.Capture.IsDisposed);
        Assert.False(audio.IsDisposed);
        Assert.Equal(before + 1, endpoint.Sent.Count);

        await coordinator.StopAsync();
        Assert.True(audio.Capture.IsDisposed);
        Assert.True(audio.IsDisposed);
    }

    [Fact]
    public async Task WarmMicrophoneStartFailureRollsBackInfrastructure()
    {
        var audio = new FakeAudioBackend(failStart: true);
        await using var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio);

        await Assert.ThrowsAsync<IOException>(() => coordinator.SetKeepMicrophoneWarmAsync(true));

        Assert.True(audio.Capture.IsDisposed);
        Assert.True(audio.IsDisposed);
    }

    [Fact]
    public async Task CallPriorityAllowsTransmitPreparationDuringReceivePlayback()
    {
        var channel = Channel("Priority", 100);
        channel.SetAudioEnabled(true);
        channel.MarkReceivePlaybackActive(sourceId: 42, streamId: 7);
        channel.SetHasCallPriority(true);
        var endpoint = new FakeEndpoint("Test", [channel]);
        var audio = new FakeAudioBackend();
        await using var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio);

        await coordinator.StartAsync(channel.ToTransmitDescriptor(), endpoint);

        Assert.Single(coordinator.ActiveChannels);
    }

    [Fact]
    public async Task SessionFaultIsReportedAndCleanupRemainsSafe()
    {
        var channel = Channel("Analog", 100);
        var endpoint = new FakeEndpoint("Test", [channel], throwOnSend: true);
        var audio = new FakeAudioBackend();
        var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio);
        Exception? fault = null;
        coordinator.Faulted += (_, exception) => fault = exception;

        await coordinator.StartAsync(channel.ToTransmitDescriptor(), endpoint);
        await coordinator.ActivateAsync();
        audio.Capture.Emit(new short[160]);
        await WaitForAsync(() => fault is not null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StopAsync());

        Assert.IsType<IOException>(fault);
        Assert.Single(coordinator.ActiveChannels);
        await Assert.ThrowsAsync<AggregateException>(() => coordinator.DisposeAsync().AsTask());
        Assert.True(audio.Capture.IsDisposed);
        Assert.Empty(coordinator.ActiveChannels);
    }

    [Fact]
    public async Task FailedTerminatorRetainsOwnershipUntilAConfirmedRetry()
    {
        var channel = Channel("Analog", 100);
        var endpoint = new FakeEndpoint("Test", [channel])
        {
            FailuresRemaining = 1
        };
        var audio = new FakeAudioBackend();
        await using var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio);

        await coordinator.StartAsync(channel.ToTransmitDescriptor(), endpoint);
        await coordinator.ActivateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StopAsync());

        Assert.Single(coordinator.ActiveChannels);
        Assert.False(audio.Capture.IsRunning);
        Assert.False(audio.Capture.IsDisposed);

        await coordinator.StopAsync();

        Assert.Empty(coordinator.ActiveChannels);
        Assert.True(audio.Capture.IsDisposed);
    }

    [Theory]
    [InlineData("analog", false)]
    [InlineData("analog", true)]
    [InlineData("dmr", false)]
    [InlineData("dmr", true)]
    [InlineData("p25", false)]
    [InlineData("p25", true)]
    [InlineData("nxdn", false)]
    [InlineData("nxdn", true)]
    public async Task MultiTargetStopGatesMicrophoneAndReleasesOtherTargetsWhileOneTerminatorIsBlocked(
        string mode, bool keepWarm)
    {
        ChannelViewModel[] channels = [Channel("A", 100, mode), Channel("B", 101, mode), Channel("C", 102, mode)];
        var endpoint = new FakeEndpoint("Test", channels);
        var audio = new FakeAudioBackend();
        int observations = 0;
        await using var coordinator = new ChannelTransmitCoordinator(
            createAudioBackend: () => audio,
            createVocoderBackend: () => new FakeVocoderBackend(),
            samplesObserver: (_, _, _, _) => Interlocked.Increment(ref observations));
        await coordinator.SetKeepMicrophoneWarmAsync(keepWarm);
        await coordinator.StartAsync(channels.Select(channel => new TransmitTarget(channel.ToTransmitDescriptor(), endpoint)));
        await coordinator.ActivateAsync();
        uint blockedStream = coordinator.GetActiveStreamId(channels[2].Id);
        using var releaseTerminator = new ManualResetEventSlim();
        var terminatorEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherTargetsStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stoppedStreams = new ConcurrentDictionary<uint, byte>();
        endpoint.BeforeSend = streamId =>
        {
            if (streamId == blockedStream)
            {
                terminatorEntered.TrySetResult();
                releaseTerminator.Wait();
                return;
            }
            stoppedStreams.TryAdd(streamId, 0);
            if (stoppedStreams.Count == 2)
                otherTargetsStopped.TrySetResult();
        };

        Task stop = Task.Run(coordinator.StopAsync);
        try
        {
            await terminatorEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            audio.Capture.Emit(new short[160]);
            Assert.Equal(0, Volatile.Read(ref observations));
            await otherTargetsStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stop.IsCompleted);
            Assert.False(audio.IsDisposed);
            Assert.Contains(channels[2].Id, coordinator.ActiveChannels);
        }
        finally
        {
            releaseTerminator.Set();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Empty(coordinator.ActiveChannels);
        Assert.Equal(keepWarm, audio.Capture.IsRunning);
    }

    [Fact]
    public async Task MultiTargetStopRetainsOnlyFailedTargetUntilRetry()
    {
        ChannelViewModel[] channels = [Channel("A", 100), Channel("B", 101), Channel("C", 102)];
        var endpoint = new FakeEndpoint("Test", channels);
        var audio = new FakeAudioBackend();
        await using var coordinator = new ChannelTransmitCoordinator(createAudioBackend: () => audio);
        await coordinator.StartAsync(channels.Select(channel => new TransmitTarget(channel.ToTransmitDescriptor(), endpoint)));
        await coordinator.ActivateAsync();
        uint failingStream = coordinator.GetActiveStreamId(channels[1].Id);
        int failuresRemaining = 1;
        var attempted = new ConcurrentDictionary<uint, byte>();
        endpoint.BeforeSend = streamId =>
        {
            attempted.TryAdd(streamId, 0);
            if (streamId == failingStream && Interlocked.Exchange(ref failuresRemaining, 0) == 1)
                throw new IOException("test terminator failure");
        };

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(coordinator.StopAsync);

        Assert.Contains("B", failure.Message, StringComparison.Ordinal);
        Assert.Equal(3, attempted.Count);
        Assert.Equal(channels[1].Id, Assert.Single(coordinator.ActiveChannels));
        Assert.False(audio.Capture.IsRunning);
        Assert.False(audio.IsDisposed);
        await coordinator.StopAsync();
        Assert.Empty(coordinator.ActiveChannels);
        Assert.Equal(1, audio.DisposeCount);
    }

    private static ChannelViewModel Channel(string name, uint tgid, string mode = "analog") => new(new ChannelConfiguration
    {
        Name = name,
        System = "Test",
        Tgid = tgid.ToString(),
        Mode = mode,
        Slot = 1
    });

    private sealed class FakeEndpoint(string name, IReadOnlyList<ChannelViewModel> channels, bool throwOnSend = false) : IFneTrafficEndpoint
    {
        private uint nextStreamId;
        public string Name => name;
        public IReadOnlyList<ChannelViewModel> Channels => channels;
        public IReadOnlyCollection<TransmitChannelDescriptor> ChannelDescriptors
            => channels.Select(channel => channel.ToTransmitDescriptor()).ToArray();
        public IReadOnlyCollection<ChannelId> ChannelIds
            => channels.Select(channel => new ChannelId(channel.SessionId)).ToArray();
        public bool IsConnected => true;
        public uint? SourceId => 1001;
        public FneTalkgroupAvailability TalkgroupAvailability { get; set; } =
            FneTalkgroupAvailability.Pending;
        public int FailuresRemaining { get; set; }
        public Action<uint>? BeforeSend { get; set; }
        public ConcurrentQueue<(FneTrafficProtocol Protocol, uint StreamId)> Sent { get; } = [];
        public uint CreateStreamId() => ++nextStreamId;
        public FneTalkgroupAvailability GetTalkgroupAvailability(
            FneTrafficProtocol protocol,
            uint destinationId,
            byte runtimeSlot)
            => TalkgroupAvailability;
        public void SendTraffic(FneTrafficProtocol protocol, ReadOnlyMemory<byte> payload, ushort sequence, uint streamId)
        {
            if (throwOnSend || FailuresRemaining > 0)
            {
                if (FailuresRemaining > 0)
                    FailuresRemaining--;
                throw new IOException("test transport fault");
            }
            BeforeSend?.Invoke(streamId);
            Sent.Enqueue((protocol, streamId));
        }
    }

    private sealed class FakeAudioBackend(
        bool failStart = false,
        string inputDeviceId = "input",
        bool? inputIsBluetooth = false)
        : IAudioBackend
    {
        public FakeCapture Capture { get; } = new(failStart);
        public int OpenCaptureCalls { get; private set; }
        public bool IsDisposed { get; private set; }
        public int DisposeCount { get; private set; }
        public string? LastInputDeviceId { get; private set; }
        public string Name => "test";
        public IReadOnlyList<AudioDeviceInfo> EnumerateDevices(AudioDirection direction)
            => [new AudioDeviceInfo(
                direction == AudioDirection.Input ? inputDeviceId : "output",
                "Test",
                direction,
                true,
                direction == AudioDirection.Input ? inputIsBluetooth : false)];
        public IAudioCapture OpenCapture(AudioDeviceInfo device, PcmAudioFormat format)
        {
            OpenCaptureCalls++;
            LastInputDeviceId = device.Id;
            return Capture;
        }
        public IAudioPlayback OpenPlayback(AudioDeviceInfo device, PcmAudioFormat format) => throw new NotSupportedException();
        public void Dispose() { IsDisposed = true; DisposeCount++; }
    }

    private sealed class FakeCapture(bool failStart = false) : IAudioCapture
    {
        public event EventHandler<PcmSamplesEventArgs>? SamplesAvailable;
        public PcmAudioFormat Format => PcmAudioFormat.Voice8KhzMono16Bit;
        public bool IsRunning { get; private set; }
        public bool IsDisposed { get; private set; }
        public int DisposeCount { get; private set; }
        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            if (failStart)
                throw new IOException("test capture start failure");
            IsRunning = true;
            return ValueTask.CompletedTask;
        }
        public ValueTask StopAsync(CancellationToken cancellationToken = default) { IsRunning = false; return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() { IsDisposed = true; DisposeCount++; return ValueTask.CompletedTask; }
        public void Emit(short[] samples) => SamplesAvailable?.Invoke(this, new PcmSamplesEventArgs(samples));
    }

    private sealed class FakeVocoderBackend(bool failCreateSession = false) : IVocoderBackend
    {
        public int FailAtSession { get; init; }
        public int DisposeCount { get; private set; }
        public List<FakeVocoderSession> Sessions { get; } = [];
        public int CreateSessionCalls { get; private set; }
        public bool IsDisposed { get; private set; }
        public string Name => "test";
        public bool IsAvailable => !IsDisposed;
        public IVocoderSession CreateSession(VocoderMode mode)
        {
            CreateSessionCalls++;
            if (failCreateSession || CreateSessionCalls == FailAtSession)
                throw new IOException("test vocoder startup failure");
            var session = new FakeVocoderSession();
            Sessions.Add(session);
            return session;
        }
        public void Dispose() { IsDisposed = true; DisposeCount++; }
    }

    private sealed class FakeVocoderSession : IVocoderSession
    {
        public int DisposeCount { get; private set; }
        public int Encode(ReadOnlySpan<short> samples, Span<byte> codeword) { codeword.Fill(0x42); return 0; }
        public int Decode(ReadOnlySpan<byte> codeword, Span<short> samples) => 0;
        public void Dispose() { DisposeCount++; }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(5);
        Assert.True(condition());
    }
}
